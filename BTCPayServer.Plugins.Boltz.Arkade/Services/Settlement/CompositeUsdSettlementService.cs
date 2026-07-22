using Boltz.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Helpers;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Safety;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public class CompositeUsdSettlementService(
    IStablecoinSwapClient stablecoinClient,
    SwapsManagementService swapsManagementService,
    ISwapStorage swapStorage,
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    ILogger<CompositeUsdSettlementService> logger)
{
    private static readonly SwapRoute ArkToLightning = new(SwapAsset.ArkBtc, SwapAsset.BtcLightning);
    // The NNark quote rounds its proportional submarine fee down while swap
    // registration rounds the lockup requirement up. One sat covers that rounding
    // boundary without creating a spend-to-self remainder during a full sweep.
    internal const long ArkFundingReserveSats = 1;

    public bool Available => stablecoinClient.IsAvailable;
    public string? UnavailableReason => stablecoinClient.UnavailableReason;

    /// <summary>
    /// The single source of the Ark→Lightning invoice-amount derivation: the
    /// Lightning amount an Ark sweep of <paramref name="amountSats"/> can fund
    /// after NNark submarine fees and the funding reserve.
    /// </summary>
    internal async Task<(long InvoiceAmountSats, long ArkFeeSats)> GetInvoiceAmountAsync(
        long amountSats,
        CancellationToken cancellationToken)
    {
        var arkQuote = await swapsManagementService.GetQuoteAsync(
            ArkToLightning,
            amountSats,
            cancellationToken);
        return (arkQuote.DestinationAmount - ArkFundingReserveSats, arkQuote.TotalFees);
    }

    public async Task<SettlementTransferResult> InitiateTransfer(
        SettlementTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!Available)
            throw new InvalidOperationException(UnavailableReason ?? "Stablecoin settlement is unavailable.");

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        if (serverInfo.Network != Network.Main && !stablecoinClient.EndpointOverridesActive)
        {
            throw new NotSupportedException(
                "Native stablecoin settlement is mainnet-only and cannot run against this Ark network.");
        }

        var nativeClient = await stablecoinClient.GetClient(request.WalletId, cancellationToken);

        await using var walletLock = await safetyService.LockKeyAsync(
            $"settlement::{request.WalletId}", cancellationToken);

        var transfer = new UsdSettlementTransferEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            StoreId = request.StoreId!,
            WalletId = request.WalletId,
            State = UsdSettlementState.PreFunding,
            DestinationNetwork = request.Destination.Network,
            DestinationAsset = UsdSettlementConfiguration.CanonicalizeAsset(request.Destination.Asset),
            DestinationAddress = request.Destination.Address,
            SourceAmountSats = request.AmountSats,
            SlippageBps = checked((int)(request.MaxSlippageBps ?? UsdSettlementData.DefaultSlippageBps)),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await InsertTransfer(transfer, cancellationToken);

        // Any failure before FundingStarted is durably persisted is provably
        // unfunded, so it terminates the row right here and the next wallet
        // event starts over with a fresh transfer. Crash windows are covered
        // by the reconciler's stale-PreFunding sweep, not by a resume path.
        try
        {
            await CreateLegs(nativeClient, request, transfer, serverInfo.Network, cancellationToken);
        }
        catch (Exception ex)
        {
            try
            {
                transfer.State = UsdSettlementState.Cancelled;
                transfer.Error = Truncate(ex.Message);
                await UpdateTransfer(transfer, cancellationToken);
                logger.LogWarning("Stablecoin settlement {TransferId} was cancelled before funding: {Reason}", transfer.Id, ex.Message);
            }
            catch (Exception cancelEx)
            {
                logger.LogWarning(
                    cancelEx,
                    "Stablecoin settlement {TransferId} failed before funding but could not be cancelled; the reconciler sweep will cancel it",
                    transfer.Id);
            }

            throw;
        }

        transfer.State = UsdSettlementState.FundingStarted;
        await UpdateTransfer(transfer, cancellationToken);

        try
        {
            var fundingTxId = await swapsManagementService.PayExistingSubmarineSwap(
                request.WalletId,
                transfer.NnarkSwapId!,
                cancellationToken);
            transfer.ArkFundingTxId = fundingTxId.ToString();
            transfer.State = UsdSettlementState.ArkLegFunded;
            await UpdateTransfer(transfer, cancellationToken);
        }
        catch (Exception ex)
        {
            transfer.State = UsdSettlementState.ManualReview;
            transfer.Error = Truncate(ex.Message);
            await UpdateTransfer(transfer, cancellationToken);
            logger.LogError(ex, "Stablecoin settlement {TransferId} requires manual review: {Reason}", transfer.Id, ex.Message);
            throw;
        }

        logger.LogInformation(
            "Initiated composite stablecoin settlement {TransferId}; wallet={WalletId} rustSwap={RustSwapId} arkSwap={ArkSwapId} arkFundingTx={ArkFundingTxId} source={SourceAmountSats} sats expected={ExpectedOutputAtomic} {DestinationAsset} atomic units",
            transfer.Id,
            transfer.WalletId,
            transfer.RustSwapId,
            transfer.NnarkSwapId,
            transfer.ArkFundingTxId,
            transfer.SourceAmountSats,
            transfer.ExpectedOutputAtomic,
            transfer.DestinationAsset);

        return ToResult(transfer);
    }

    /// <summary>
    /// Quote, create the native reverse swap, and register the Ark submarine
    /// leg — all while the row stays PreFunding, so any throw is provably
    /// unfunded and the caller cancels the row.
    /// </summary>
    private async Task CreateLegs(
        IBoltzClient nativeClient,
        SettlementTransferRequest request,
        UsdSettlementTransferEntity transfer,
        Network network,
        CancellationToken cancellationToken)
    {
        var (nativeInvoiceAmount, arkFeeSats) = await GetInvoiceAmountAsync(
            request.AmountSats,
            cancellationToken);
        if (nativeInvoiceAmount <= 0)
            throw new InvalidOperationException("Ark settlement amount does not cover the submarine swap fees.");

        var bindingAsset = UsdSettlementConfiguration.ResolveBindingAsset(
                nativeClient.DestinationsAccepting(request.Destination.Address),
                request.Destination.Network,
                request.Destination.Asset)
            ?? throw new InvalidOperationException(
                $"{request.Destination.Asset} is not supported on {request.Destination.Network} for this address.");
        var prepared = await nativeClient.PrepareFromSats(
            request.Destination.Address,
            request.Destination.Network,
            bindingAsset,
            checked((ulong)nativeInvoiceAmount),
            request.MaxSlippageBps);
        transfer.InvoiceAmountSats = checked((long)prepared.InvoiceAmountSats);
        transfer.ExpectedOutputAtomic = checked((long)prepared.OutputAmount);
        transfer.StableLegFeeSats = checked((long)prepared.BoltzFeeSats);
        transfer.ArkLegFeeSats = arkFeeSats;

        var created = await nativeClient.CreateReverseSwap(prepared);
        // Record the native linkage before validating the response, so even a
        // mismatch cancel keeps the exact swap id in the ledger.
        transfer.RustSwapId = created.SwapId;
        transfer.Invoice = created.Invoice;
        var invoice = Bolt11Helper.TryParse(created.Invoice, network)
            ?? throw new InvalidOperationException("The stablecoin reverse swap returned an invalid BOLT11 invoice.");
        var invoiceAmount = checked((long)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0));
        if (invoiceAmount != transfer.InvoiceAmountSats ||
            created.InvoiceAmountSats != prepared.InvoiceAmountSats)
        {
            throw new InvalidOperationException(
                "The stablecoin reverse-swap invoice amount does not match its quote.");
        }

        transfer.PaymentHash = invoice.Hash.ToString();
        await UpdateTransfer(transfer, cancellationToken);

        transfer.NnarkSwapId = await swapsManagementService.InitiateSubmarineSwap(
            request.WalletId,
            invoice,
            autoPay: false,
            cancellationToken);
        var arkSwap = await GetArkSwap(request.WalletId, transfer.NnarkSwapId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Registered Ark submarine swap {transfer.NnarkSwapId} was not persisted.");
        if (arkSwap.ExpectedAmount > request.AmountSats)
            throw new InvalidOperationException(
                $"Ark submarine lockup requires {arkSwap.ExpectedAmount} sats, exceeding the reserved {request.AmountSats} sats.");
        await UpdateTransfer(transfer, cancellationToken);
    }

    // Error text is an operator-facing failure summary, not a stack-trace
    // archive: truncate at write so pathological exception text can never
    // balloon the row or the API payload.
    internal static string? Truncate(string? value, int max = 2000) =>
        value is not null && value.Length > max ? value[..max] : value;

    private static void ValidateRequest(SettlementTransferRequest request)
    {
        if (request.AmountSats <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.AmountSats), "Settlement amount must be positive.");
        _ = UsdSettlementConfiguration.CanonicalizeAsset(request.Destination.Asset);
        if (string.IsNullOrWhiteSpace(request.StoreId))
            throw new ArgumentException("StoreId is required for stablecoin settlement.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Destination.Network) ||
            string.IsNullOrWhiteSpace(request.Destination.Address))
            throw new ArgumentException("Stablecoin destination chain and address are required.", nameof(request));
        if (request.MaxSlippageBps is < UsdSettlementData.MinSlippageBps or > UsdSettlementData.MaxSlippageBps)
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxSlippageBps),
                $"Slippage must be between {UsdSettlementData.MinSlippageBps} and {UsdSettlementData.MaxSlippageBps} basis points.");
    }

    private async Task InsertTransfer(
        UsdSettlementTransferEntity transfer,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.UsdSettlementTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateTransfer(
        UsdSettlementTransferEntity transfer,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        transfer.UpdatedAt = DateTimeOffset.UtcNow;
        db.Attach(transfer);
        db.Entry(transfer).State = EntityState.Modified;
        // The xmin loaded with this detached entity is the concurrency token the
        // UPDATE must compare against; EF refreshes it after a successful save.
        db.Entry(transfer).Property(entity => entity.Xmin).OriginalValue = transfer.Xmin;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Operator dismissal of a ManualReview row — the only operator-driven exit
    /// from the operator-owned state, shared by the Greenfield cancel endpoint
    /// and the store-overview Cancel button. Cancelling frees the wallet for
    /// new settlements while keeping the original failure text as the audit
    /// trail; the operator asserts the funds were verified manually. Returns
    /// null when no transfer with that id exists for the store; throws
    /// <see cref="InvalidOperationException"/> for any automation-owned state.
    /// </summary>
    public async Task<UsdSettlementTransferEntity?> CancelManualReviewTransfer(
        string storeId,
        string transferId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var transfer = await db.UsdSettlementTransfers.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == transferId && candidate.StoreId == storeId,
                cancellationToken);
        if (transfer is null)
            return null;

        if (!transfer.State.IsOperatorOwned())
            throw new InvalidOperationException(
                $"Stablecoin settlement {transfer.Id} is in state {transfer.State} and cannot be cancelled: only transfers awaiting manual review are operator-owned — automation owns every other state.");

        // Keep Error untouched: the original failure text is the record of why
        // the row needed review.
        transfer.State = UsdSettlementState.Cancelled;
        await UpdateTransfer(transfer, cancellationToken);
        logger.LogWarning(
            "Stablecoin settlement {TransferId} was dismissed from manual review by an operator: {Error}",
            transfer.Id,
            transfer.Error);
        return transfer;
    }

    private async Task<NArk.Swaps.Models.ArkSwap?> GetArkSwap(
        string walletId,
        string swapId,
        CancellationToken cancellationToken)
    {
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId],
            swapIds: [swapId],
            cancellationToken: cancellationToken);
        return swaps.SingleOrDefault();
    }

    private static SettlementTransferResult ToResult(UsdSettlementTransferEntity transfer) =>
        new(
            transfer.Id,
            transfer.SourceAmountSats,
            DestinationAmountSats: 0,
            FeesPaidSats: (transfer.StableLegFeeSats ?? 0) + (transfer.ArkLegFeeSats ?? 0),
            DestinationAtomicAmount: transfer.DeliveredOutputAtomic ?? transfer.ExpectedOutputAtomic);
}
