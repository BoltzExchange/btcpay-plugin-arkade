using System.Linq.Expressions;
using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Safety;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public sealed class UsdSettlementReconciliationService(
    IStablecoinSwapClient stablecoinClient,
    ISwapStorage swapStorage,
    ISafetyService safetyService,
    SettlementSchedulerService settlementScheduler,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    ILogger<UsdSettlementReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly HashSet<string> _resumedWallets = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!stablecoinClient.IsAvailable)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Reconcile(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reconcile stablecoin settlement state");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    // Terminal rows need no reconciliation; everything else is either an
    // in-flight swap to mirror or crash debris for the stale handling below.
    internal static readonly Expression<Func<UsdSettlementTransferEntity, bool>> ReconciliationScope =
        transfer =>
            transfer.State != UsdSettlementState.Completed &&
            transfer.State != UsdSettlementState.Refunded &&
            transfer.State != UsdSettlementState.Cancelled;

    // The one crash allowance: how long a PreFunding or FundingStarted row may
    // sit unchanged before it is treated as a crashed attempt rather than a
    // live pass still holding the wallet lock. The window is measured against
    // UpdatedAt, so it only ever expires on rows nothing is driving anymore.
    internal static readonly TimeSpan RecoveryGracePeriod = TimeSpan.FromMinutes(10);

    internal static bool IsPastRecoveryGrace(
        UsdSettlementTransferEntity transfer,
        DateTimeOffset now) =>
        now - transfer.UpdatedAt >= RecoveryGracePeriod;

    private async Task Reconcile(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var transfers = await db.UsdSettlementTransfers
            .Where(ReconciliationScope)
            .ToArrayAsync(cancellationToken);

        foreach (var walletTransfers in transfers.GroupBy(transfer => transfer.WalletId))
        {
            var walletBecameTerminal = false;
            await using var walletLock = await safetyService.LockKeyAsync(
                $"settlement::{walletTransfers.Key}", cancellationToken);
            foreach (var transfer in walletTransfers)
                await db.Entry(transfer).ReloadAsync(cancellationToken);

            IBoltzClient client;
            try
            {
                client = await stablecoinClient.GetClient(walletTransfers.Key, cancellationToken);
                if (!_resumedWallets.Contains(walletTransfers.Key))
                {
                    var resumed = await client.ResumeSwaps();
                    _resumedWallets.Add(walletTransfers.Key);
                    if (resumed.Length > 0)
                    {
                        logger.LogInformation(
                            "Resumed {SwapCount} native stablecoin swaps for Arkade wallet {WalletId}",
                            resumed.Length,
                            walletTransfers.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to initialize or resume native stablecoin reconciliation for Arkade wallet {WalletId}",
                    walletTransfers.Key);
                continue;
            }

            try
            {
                var events = client.DrainEvents();
                var degraded = events
                    .OfType<BindingEvent.QuoteDegraded>()
                    .GroupBy(@event => @event.Swap.Id)
                    .ToDictionary(group => group.Key, group => group.Last());
                if (events.Any(@event => @event is BindingEvent.ResyncRequired))
                {
                    logger.LogWarning(
                        "Native stablecoin event queue overflowed for Arkade wallet {WalletId}; performing a full durable-state resync",
                        walletTransfers.Key);
                }

                foreach (var transfer in walletTransfers)
                {
                    // A PreFunding row is crash debris: the funding path either
                    // advances or cancels its own row before returning. It is
                    // provably unfunded, so a stale one just cancels; a young
                    // one may still be mid-pass and is left alone.
                    if (transfer.State == UsdSettlementState.PreFunding)
                    {
                        if (IsPastRecoveryGrace(transfer, DateTimeOffset.UtcNow))
                        {
                            transfer.State = UsdSettlementState.Cancelled;
                            transfer.Error = "Cancelled automatically: the settlement attempt did not survive to funding.";
                            transfer.UpdatedAt = DateTimeOffset.UtcNow;
                            walletBecameTerminal = true;
                        }

                        continue;
                    }

                    var swap = await client.GetSwap(transfer.RustSwapId!);
                    if (swap is null)
                    {
                        if (Transition(
                                transfer,
                                UsdSettlementState.ManualReview,
                                $"Native stablecoin swap {transfer.RustSwapId} is missing from durable storage."))
                            transfer.UpdatedAt = DateTimeOffset.UtcNow;
                        continue;
                    }

                    var arkSwap = await FindArkSwap(transfer, cancellationToken);
                    var changed = ApplySwapState(transfer, swap, arkSwap);
                    if (degraded.TryGetValue(swap.Id, out var quoteDegraded))
                        changed |= await HandleQuoteDegraded(transfer, swap, quoteDegraded, client, logger);

                    // A FundingStarted row past the grace whose swap never saw
                    // the payment is a crashed funding attempt; whether its
                    // broadcast exists is not locally decidable, so it parks
                    // for the operator. Funded ones self-advance above the
                    // moment the swap reports InvoicePaid.
                    if (transfer.State == UsdSettlementState.FundingStarted &&
                        swap.Status is BindingSwapStatus.Created &&
                        IsPastRecoveryGrace(transfer, DateTimeOffset.UtcNow))
                    {
                        changed |= Transition(
                            transfer,
                            UsdSettlementState.ManualReview,
                            "Ark funding was previously started without a durable result; inspect NNark VTXOs/intents before taking action.");
                    }

                    if (!changed)
                        continue;

                    transfer.UpdatedAt = DateTimeOffset.UtcNow;
                    if (transfer.State.IsTerminal())
                        walletBecameTerminal = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to reconcile native stablecoin swaps for Arkade wallet {WalletId}",
                    walletTransfers.Key);
            }

            // Tracked entities save under the xmin concurrency token; a concurrent
            // writer surfaces as DbUpdateConcurrencyException. Handle it per wallet
            // group — and reload this group's entries so the shared context carries
            // no stale modifications into the next group's save — so one wallet's
            // race defers only that wallet to the next pass instead of aborting the
            // remaining groups.
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogWarning(
                    ex,
                    "Concurrent settlement write for Arkade wallet {WalletId}; retrying on the next pass",
                    walletTransfers.Key);
                foreach (var transfer in walletTransfers)
                    await db.Entry(transfer).ReloadAsync(cancellationToken);
                continue;
            }

            if (walletBecameTerminal)
                settlementScheduler.QueueWallet(walletTransfers.Key);
        }
    }

    private async Task<ArkSwap?> FindArkSwap(
        UsdSettlementTransferEntity transfer,
        CancellationToken cancellationToken)
    {
        // NnarkSwapId is durably persisted before FundingStarted, so every row
        // that reaches per-swap reconciliation carries it.
        if (transfer.NnarkSwapId is null)
            return null;

        return (await swapStorage.GetSwaps(
            walletIds: [transfer.WalletId],
            swapIds: [transfer.NnarkSwapId],
            cancellationToken: cancellationToken)).SingleOrDefault();
    }

    /// <summary>
    /// Consumes a drained QuoteDegraded event by accepting the degraded quote.
    /// The merchant never locked an exchange rate — the threshold triggers a
    /// market-rate sweep — so a degraded quote is just the current market and
    /// the settlement proceeds at it; the slippage bounds remain enforced
    /// inside the claim itself. Acceptance is legal only while the durable
    /// swap lookup shows TbtcLocked/Claiming; the resulting state is picked up
    /// by the normal Advance path on the next tick (a failed forced claim
    /// leaves the swap Claiming for the manager's automatic retry).
    /// </summary>
    internal static async Task<bool> HandleQuoteDegraded(
        UsdSettlementTransferEntity transfer,
        BindingSwap swap,
        BindingEvent.QuoteDegraded quoteDegraded,
        IBoltzClient client,
        ILogger logger)
    {
        if (swap.Status is not (BindingSwapStatus.TbtcLocked or BindingSwapStatus.Claiming))
            return false;

        logger.LogWarning(
            "Stablecoin settlement {TransferId}: accepting degraded quote for swap {SwapId} — expected {ExpectedUsd} USD atomic units, quoted {QuotedUsd}",
            transfer.Id,
            swap.Id,
            quoteDegraded.ExpectedUsd,
            quoteDegraded.QuotedUsd);
        try
        {
            await client.AcceptDegradedQuote(swap.Id);
        }
        catch (BindingException.Operation ex) when (ex.code == "generic")
        {
            // The swap left the acceptable window between the durable lookup
            // and this call — it already progressed, so there is nothing left
            // to accept and the next tick reads whatever it became.
            logger.LogInformation(
                "Stablecoin settlement {TransferId}: swap {SwapId} progressed past its degraded quote before acceptance: {Message}",
                transfer.Id,
                swap.Id,
                ex.message);
        }

        return false;
    }

    internal static bool ApplySwapState(
        UsdSettlementTransferEntity transfer,
        BindingSwap swap,
        ArkSwap? arkSwap)
    {
        var changed = CopyBridgeFacts(transfer, swap);

        if (swap.Status is BindingSwapStatus.Completed)
        {
            var delivered = checked((long)(swap.DeliveredAmount ?? swap.ExpectedOutputAmount));
            if (transfer.DeliveredOutputAtomic != delivered)
            {
                transfer.DeliveredOutputAtomic = delivered;
                changed = true;
            }

            if (arkSwap?.Status is ArkSwapStatus.Failed or ArkSwapStatus.Refunded)
            {
                // Stablecoin delivery is authoritative value-transfer
                // evidence. Never label the composite as refunded once the
                // destination received funds, even if NNark reports a
                // conflicting terminal state — that contradiction is exactly
                // what an operator has to reconcile.
                return Transition(
                    transfer,
                    UsdSettlementState.ManualReview,
                    CompositeUsdSettlementService.Truncate(
                        arkSwap.FailReason ??
                        $"Native delivery completed but the Ark leg is {arkSwap.Status}.")) | changed;
            }

            // Destination delivery is the authoritative completion signal. A
            // missing or lagging NNark row must not reserve the wallet forever.
            return Transition(transfer, UsdSettlementState.Completed, error: null) | changed;
        }

        if (arkSwap?.Status == ArkSwapStatus.Refunded)
        {
            if (swap.Status is BindingSwapStatus.Created or BindingSwapStatus.Expired or BindingSwapStatus.Failed)
            {
                return Transition(
                    transfer,
                    UsdSettlementState.Refunded,
                    CompositeUsdSettlementService.Truncate(
                        arkSwap.FailReason ?? "The Ark funding leg was refunded.")) | changed;
            }

            // Once the native leg has observed payment, an NNark refund is
            // contradictory rather than proof that all value returned.
            return Transition(
                transfer,
                UsdSettlementState.ManualReview,
                CompositeUsdSettlementService.Truncate(
                    arkSwap.FailReason ??
                    $"The Ark funding leg reports Refunded while the native stablecoin leg is {swap.Status}.")) | changed;
        }

        if (arkSwap?.Status == ArkSwapStatus.Failed)
        {
            // The submarine funding swap reports failure; whether VTXOs moved
            // is not locally decidable, so escalate rather than fail hard.
            return Transition(
                transfer,
                UsdSettlementState.ManualReview,
                CompositeUsdSettlementService.Truncate(
                    arkSwap.FailReason ?? "The Ark funding leg failed.")) | changed;
        }

        switch (swap.Status)
        {
            // BindingSwapStatus.Created has nothing to advance: PreFunding is
            // the initial state, and FundingStarted is written only by the
            // funding path, never by reconciliation.
            case BindingSwapStatus.InvoicePaid:
                changed |= Advance(transfer, UsdSettlementState.ArkLegFunded);
                break;
            case BindingSwapStatus.TbtcLocked:
                changed |= Advance(transfer, UsdSettlementState.TbtcLocked);
                break;
            case BindingSwapStatus.Claiming:
                changed |= Advance(transfer, UsdSettlementState.StableClaiming);
                break;
            case BindingSwapStatus.Settling:
                changed |= Advance(transfer, UsdSettlementState.BridgeSettling);
                break;
            case BindingSwapStatus.Failed failed:
                changed |= Transition(
                    transfer,
                    UsdSettlementState.ManualReview,
                    CompositeUsdSettlementService.Truncate(failed.Reason));
                break;
            case BindingSwapStatus.Expired:
                changed |= Transition(
                    transfer,
                    UsdSettlementState.ManualReview,
                    "The stablecoin reverse swap expired.");
                break;
        }

        return changed;
    }

    private static bool CopyBridgeFacts(UsdSettlementTransferEntity transfer, BindingSwap swap)
    {
        var changed = false;
        var bridgeKind = swap.BridgeKind.ToString();
        if (transfer.BridgeKind != bridgeKind)
        {
            transfer.BridgeKind = bridgeKind;
            changed = true;
        }

        if (transfer.TbtcLockupTxId != swap.LockupTxId)
        {
            transfer.TbtcLockupTxId = swap.LockupTxId;
            changed = true;
        }

        if (transfer.ArbitrumClaimTxHash != swap.ClaimTxHash)
        {
            transfer.ArbitrumClaimTxHash = swap.ClaimTxHash;
            changed = true;
        }

        if (transfer.BridgeRef != swap.BridgeRef)
        {
            transfer.BridgeRef = swap.BridgeRef;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Single chokepoint for every reconciler-driven cross-category state
    /// write. Terminal states are never left automatically; ManualReview is
    /// operator-owned and resolvable only by definitive external evidence
    /// (Completed / Refunded). A same-state call refreshes the failure
    /// message on automation-owned states but never rewrites the message of
    /// an operator-owned one.
    /// </summary>
    private static bool Transition(
        UsdSettlementTransferEntity transfer,
        UsdSettlementState next,
        string? error)
    {
        if (transfer.State == next)
        {
            if (transfer.State.IsOperatorOwned() || transfer.Error == error)
                return false;
            transfer.Error = error;
            return true;
        }

        var allowed = transfer.State.IsTerminal()
            ? false
            : !transfer.State.IsOperatorOwned() ||
              next is UsdSettlementState.Completed or UsdSettlementState.Refunded;
        if (!allowed)
            return false;

        transfer.State = next;
        transfer.Error = error;
        return true;
    }

    /// <summary>
    /// Forward-only progression within the in-flight pipeline. Never touches
    /// Error and never leaves an operator-owned or terminal state.
    /// </summary>
    private static bool Advance(
        UsdSettlementTransferEntity transfer,
        UsdSettlementState state)
    {
        if (transfer.State.IsTerminal() || transfer.State.IsOperatorOwned())
            return false;

        if (PipelinePosition(state) <= PipelinePosition(transfer.State))
            return false;

        transfer.State = state;
        return true;
    }

    private static int PipelinePosition(UsdSettlementState state) => state switch
    {
        UsdSettlementState.PreFunding => 0,
        UsdSettlementState.FundingStarted => 1,
        UsdSettlementState.ArkLegFunded => 2,
        UsdSettlementState.TbtcLocked => 3,
        UsdSettlementState.StableClaiming => 4,
        UsdSettlementState.BridgeSettling => 5,
        UsdSettlementState.Completed => 6,
        // These states never enter Advance. Keeping them outside the pipeline
        // makes an accidental call a no-op instead of depending on enum order.
        UsdSettlementState.Refunded or
            UsdSettlementState.Cancelled or
            UsdSettlementState.ManualReview => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
