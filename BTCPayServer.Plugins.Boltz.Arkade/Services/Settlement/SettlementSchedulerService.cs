using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

/// <summary>
/// The shared settlement trigger engine: watches wallet activity, decides when
/// a configured settlement rule fires and how much it moves, then dispatches
/// execution to the configured settlement option (mainchain or stablecoin).
/// </summary>
public class SettlementSchedulerService(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ISpendingService spendingService,
    IIntentStorage intentStorage,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    ISwapStorage swapStorage,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    ISettlementService settlementService,
    IStablecoinSwapClient stablecoinSwapClient,
    CompositeUsdSettlementService usdSettlementService,
    MainchainSettlementService mainchainSettlementService,
    ILogger<SettlementSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan SettlementCheckDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SwapFundingGracePeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15);

    private readonly Channel<string> _walletQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queuedWallets = new();

    public void QueueWallet(string walletId)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return;

        if (!_queuedWallets.TryAdd(walletId, 0))
            return;

        if (!_walletQueue.Writer.TryWrite(walletId))
            _queuedWallets.TryRemove(walletId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        intentStorage.IntentChanged += OnIntentChanged;
        swapStorage.SwapsChanged += OnSwapChanged;

        try
        {
            var heartbeat = QueueConfiguredWalletsLoop(stoppingToken);

            while (await _walletQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_walletQueue.Reader.TryRead(out var walletId))
                {
                    _queuedWallets.TryRemove(walletId, out _);
                    await Task.Delay(SettlementCheckDelay, stoppingToken);
                    await ProcessWalletSafely(walletId, stoppingToken);
                }
            }

            await heartbeat;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            vtxoStorage.VtxosChanged -= OnVtxoChanged;
            intentStorage.IntentChanged -= OnIntentChanged;
            swapStorage.SwapsChanged -= OnSwapChanged;
        }
    }

    // Wallet events drive settlement promptly; this loop is the dumb retry
    // behind them, re-checking every configured wallet so a transiently failed
    // or missed attempt fires again without any per-row resume machinery.
    private async Task QueueConfiguredWalletsLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await QueueConfiguredWallets(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue configured settlement wallets; retrying");
            }

            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var contracts = await contractStorage.GetContracts(
                scripts: [vtxo.Script],
                cancellationToken: CancellationToken.None);

            foreach (var walletId in contracts.Select(c => c.WalletIdentifier).Distinct())
                QueueWallet(walletId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue settlement check for VTXO {OutPoint}", vtxo.OutPoint);
        }
    }

    private void OnSwapChanged(object? sender, ArkSwap swap)
    {
        QueueWallet(swap.WalletId);
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        if (intent.State is ArkIntentState.BatchSucceeded or ArkIntentState.BatchFailed or ArkIntentState.Cancelled)
            QueueWallet(intent.WalletId);
    }

    private async Task QueueConfiguredWallets(CancellationToken cancellationToken)
    {
        var configs = await GetSettlementConfigs(cancellationToken);
        foreach (var walletId in configs.Select(config => config.Config.WalletId).Distinct())
            QueueWallet(walletId);
    }

    private async Task ProcessWalletSafely(string walletId, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessWallet(walletId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process settlement trigger for Arkade wallet {WalletId}", walletId);
        }
    }

    private async Task ProcessWallet(string walletId, CancellationToken cancellationToken)
    {
        // Ambiguous/manual recovery and pre-funding rows reserve this wallet.
        // This also protects a row created before an operator disables the
        // option: returned or still-unspent VTXOs
        // must not feed a second automatic settlement.
        if (await HasBlockingUsdTransfer(walletId, cancellationToken))
            return;

        var configs = (await GetSettlementConfigs(cancellationToken))
            .Where(config => config.Config.WalletId == walletId)
            .OrderBy(config => config.ThresholdSats)
            .ThenBy(config => config.Type)
            .ThenBy(config => config.Store.Id)
            .ToArray();

        if (configs.Length == 0)
            return;

        if (await HasBlockingActiveSwap(walletId, cancellationToken))
            return;

        var availableBalanceSats = await GetAvailableBalanceSats(walletId, cancellationToken);
        StoreSettlementConfig? trigger = null;
        long? settlementAmountSats = null;
        foreach (var candidate in configs.Where(config =>
                     availableBalanceSats >= config.ThresholdSats))
        {
            settlementAmountSats = await GetSettlementTransferAmount(
                walletId,
                availableBalanceSats,
                candidate.Type,
                candidate.ThresholdSats,
                cancellationToken);
            if (settlementAmountSats is null)
                continue;

            trigger = candidate;
            break;
        }

        if (trigger is null || settlementAmountSats is null)
            return;

        if (trigger.Type == StoreSettlementOption.Usd)
        {
            await SettleUsd(trigger, settlementAmountSats.Value, availableBalanceSats, cancellationToken);
            return;
        }

        await mainchainSettlementService.SettleWallet(
            walletId,
            trigger.Store.Id,
            trigger.ThresholdSats,
            settlementAmountSats.Value,
            availableBalanceSats,
            cancellationToken);
    }

    private async Task<bool> HasBlockingUsdTransfer(
        string walletId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UsdSettlementTransfers.AnyAsync(
            transfer =>
                transfer.WalletId == walletId &&
                (transfer.State == UsdSettlementState.PreFunding ||
                 transfer.State == UsdSettlementState.FundingStarted ||
                 transfer.State == UsdSettlementState.ArkLegFunded ||
                 transfer.State == UsdSettlementState.TbtcLocked ||
                 transfer.State == UsdSettlementState.StableClaiming ||
                 transfer.State == UsdSettlementState.BridgeSettling ||
                 transfer.State == UsdSettlementState.ManualReview),
            cancellationToken);
    }

    private async Task SettleUsd(
        StoreSettlementConfig trigger,
        long settlementAmountSats,
        long availableBalanceSats,
        CancellationToken cancellationToken)
    {
        var usd = trigger.Usd ?? throw new InvalidOperationException("Stablecoin settlement configuration is missing.");

        try
        {
            var result = await settlementService.InitiateTransfer(
                new SettlementTransferRequest(
                    trigger.Config.WalletId,
                    settlementAmountSats,
                    SettlementDestination.Stablecoin(
                        usd.DestinationChain,
                        usd.Asset,
                        usd.DestinationAddress),
                    trigger.Store.Id,
                    MaxSlippageBps: checked((uint)usd.SlippageBps)),
                cancellationToken);

            logger.LogInformation(
                "Arkade wallet {WalletId} triggered {DestinationAsset} settlement {TransferId} for store {StoreId}; source={SourceAmountSats} sats expected={DestinationAtomicAmount} atomic units fees={FeesPaidSats} sats",
                trigger.Config.WalletId,
                usd.Asset,
                result.TransferId,
                trigger.Store.Id,
                result.SourceAmountSats,
                result.DestinationAtomicAmount ?? result.DestinationAmountSats,
                result.FeesPaidSats);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to settle Arkade wallet {WalletId} balance {AvailableBalanceSats} sats to {DestinationAsset} on {DestinationChain} for store {StoreId} with amount {SettlementAmountSats} sats",
                trigger.Config.WalletId,
                availableBalanceSats,
                usd.Asset,
                usd.DestinationChain,
                trigger.Store.Id,
                settlementAmountSats);
        }
    }

    private async Task<long?> GetSettlementTransferAmount(
        string walletId,
        long availableBalanceSats,
        StoreSettlementOption type,
        long thresholdSats,
        CancellationToken cancellationToken)
    {
        if (type == StoreSettlementOption.Usd)
        {
            return await GetUsdSettlementTransferAmount(
                walletId,
                availableBalanceSats,
                thresholdSats,
                cancellationToken);
        }

        return await mainchainSettlementService.GetSettlementTransferAmount(
            walletId,
            availableBalanceSats,
            cancellationToken);
    }

    private async Task<long?> GetUsdSettlementTransferAmount(
        string walletId,
        long availableBalanceSats,
        long thresholdSats,
        CancellationToken cancellationToken)
    {
        // The threshold only gates when a settlement fires, never how much it
        // moves: every settlement sweeps the full available balance. Zero still
        // means "fire as soon as the post-NNark-fee invoice reaches the native
        // stablecoin minimum"; positive thresholds fire less often but must
        // equally clear that minimum — otherwise the transfer would be created
        // only to fail deterministically at the quote.
        var amount = availableBalanceSats;
        if (amount <= 0)
            return null;

        try
        {
            var (invoiceAmount, _) = await usdSettlementService.GetInvoiceAmountAsync(amount, cancellationToken);
            var nativeClient = await stablecoinSwapClient.GetClient(walletId, cancellationToken);
            var limits = await nativeClient.GetLimits();
            if (invoiceAmount < checked((long)limits.MinSats))
            {
                // With a configured threshold this is an operator problem (the rule can
                // never fire), not just a balance that has yet to grow.
                logger.Log(
                    thresholdSats > 0 ? LogLevel.Warning : LogLevel.Debug,
                    "Arkade wallet {WalletId} settlement of {AmountSats} sats produces a {InvoiceAmountSats} sat invoice below the swap minimum {MinAmountSats} sats",
                    walletId,
                    amount,
                    invoiceAmount,
                    limits.MinSats);
                return null;
            }

            var maxSats = checked((long)limits.MaxSats);
            if (invoiceAmount <= maxSats)
                return amount;

            logger.LogInformation(
                "Arkade wallet {WalletId} has {AvailableBalanceSats} sats available for stablecoin settlement, capping transfer towards the swap maximum {MaxAmountSats} sats",
                walletId,
                availableBalanceSats,
                maxSats);
            // Scale the source amount proportionally instead of subtracting the
            // invoice-domain excess: with an affine fee (proportional + flat)
            // the subtraction leaves the requote above the maximum by
            // excess * feePercentage forever, stalling exactly the largest
            // sweeps. Proportional scaling always lands the invoice at or
            // below the maximum in one step; the requote then verifies.
            amount = (long)((decimal)amount * maxSats / invoiceAmount);
            if (amount <= 0)
            {
                logger.LogWarning(
                    "Arkade wallet {WalletId} cannot cap its stablecoin settlement below the swap maximum {MaxAmountSats} sats; skipping this pass",
                    walletId,
                    maxSats);
                return null;
            }

            (invoiceAmount, _) = await usdSettlementService.GetInvoiceAmountAsync(amount, cancellationToken);
            if (invoiceAmount > maxSats || invoiceAmount < checked((long)limits.MinSats))
            {
                logger.LogWarning(
                    "Arkade wallet {WalletId} capped stablecoin settlement of {AmountSats} sats produces a {InvoiceAmountSats} sat invoice outside the swap limits {MinAmountSats}-{MaxAmountSats} sats; skipping this pass",
                    walletId,
                    amount,
                    invoiceAmount,
                    limits.MinSats,
                    maxSats);
                return null;
            }

            return amount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Arkade wallet {WalletId} has not yet reached the effective stablecoin swap minimum",
                walletId);
        }

        return null;
    }

    private async Task<IReadOnlyCollection<StoreSettlementConfig>> GetSettlementConfigs(
        CancellationToken cancellationToken)
    {
        if (!settlementService.Available)
            return [];

        var configs = new List<StoreSettlementConfig>();
        var stores = await storeRepository.GetStores();
        foreach (var store in stores)
        {
            var config = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId,
                paymentMethodHandlerDictionary);

            if (string.IsNullOrWhiteSpace(config?.WalletId))
                continue;

            // Settlement is mutually exclusive: only the store's active method
            // moves funds. A dormant method's config remains stored but inert.
            var active = config.ResolveActiveSettlement();

            if (active == StoreSettlementOption.BitcoinMainchain &&
                MainchainSettlementService.GetConfiguredThreshold(config) is > 0 and var mainchainThreshold)
            {
                configs.Add(new StoreSettlementConfig(
                    store,
                    config,
                    StoreSettlementOption.BitcoinMainchain,
                    mainchainThreshold));
            }

            if (active == StoreSettlementOption.Usd &&
                stablecoinSwapClient.IsAvailable &&
                UsdSettlementConfiguration.Get(config) is { } usd)
            {
                configs.Add(new StoreSettlementConfig(
                    store,
                    config,
                    StoreSettlementOption.Usd,
                    usd.ThresholdSats,
                    usd));
            }
        }

        return configs;
    }

    private async Task<bool> HasBlockingActiveSwap(string walletId, CancellationToken cancellationToken)
    {
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId],
            active: true,
            cancellationToken: cancellationToken);

        if (swaps.Count == 0)
            return false;

        var fundingCutoff = DateTimeOffset.UtcNow - SwapFundingGracePeriod;
        var potentiallyFunding = swaps
            .Where(swap => IsRecentlyCreatedOutgoingSwap(swap, fundingCutoff))
            .ToArray();
        if (potentiallyFunding.Length == 0)
            return false;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ignorableSwapIds = await db.UsdSettlementTransfers.AsNoTracking()
            .Where(transfer =>
                transfer.WalletId == walletId &&
                transfer.ArkFundingTxId == null &&
                transfer.NnarkSwapId != null &&
                transfer.State == UsdSettlementState.Cancelled)
            .Select(transfer => transfer.NnarkSwapId!)
            .ToHashSetAsync(cancellationToken);

        return potentiallyFunding.Any(swap => !ignorableSwapIds.Contains(swap.SwapId));
    }

    internal static bool IsRecentlyCreatedOutgoingSwap(ArkSwap swap, DateTimeOffset fundingCutoff) =>
        swap.SwapType is ArkSwapType.Submarine or ArkSwapType.ChainArkToBtc &&
        swap.CreatedAt >= fundingCutoff;

    private async Task<long> GetAvailableBalanceSats(string walletId, CancellationToken cancellationToken)
    {
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
        var coins = await spendingService.GetAvailableCoins(walletId, cancellationToken);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, cancellationToken);
        var lockedSet = lockedOutpoints.ToHashSet();

        return coins
            .Where(coin =>
                !coin.Unrolled &&
                !coin.IsRecoverable(currentTime) &&
                !lockedSet.Contains(coin.Outpoint))
            .Sum(coin => coin.Amount.Satoshi);
    }

    private record StoreSettlementConfig(
        StoreData Store,
        ArkadePaymentMethodConfig Config,
        StoreSettlementOption Type,
        long ThresholdSats,
        UsdSettlementConfig? Usd = null);
}
