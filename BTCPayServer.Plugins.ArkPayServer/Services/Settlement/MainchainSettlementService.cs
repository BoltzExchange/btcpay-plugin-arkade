using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NArk.Hosting;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

public class MainchainSettlementService(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ISpendingService spendingService,
    IIntentStorage intentStorage,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    ISwapStorage swapStorage,
    WalletRepository walletRepository,
    WalletReceiveService walletReceiveService,
    ISettlementService settlementService,
    BoltzLimitsValidator? boltzLimitsValidator,
    ArkNetworkConfig arkNetworkConfig,
    ILogger<MainchainSettlementService> logger) : BackgroundService, ISettlementOption
{
    private static readonly TimeSpan SettlementCheckDelay = TimeSpan.FromMilliseconds(250);
    private const string MainchainSettlementAddressLabel = "Arkade settlement swap";
    private const string SaveCommand = "settlement:bitcoin-mainchain:save";
    private const string DisableCommand = "settlement:bitcoin-mainchain:disable";

    private readonly Channel<string> _walletQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queuedWallets = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _walletLocks = new();

    public StoreSettlementOption Type => StoreSettlementOption.BitcoinMainchain;

    public async Task<SettlementOptionModel> CreateViewModel(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        var available = IsAvailable(store, config, out var unavailableReason);
        var configuredThreshold = GetConfiguredThreshold(config);
        var thresholdValue = input?.Get(MainchainSettlementData.ThresholdKey) ??
            configuredThreshold?.ToString(CultureInfo.InvariantCulture);
        var data = new JObject();
        if (thresholdValue is not null)
            data[MainchainSettlementData.ThresholdKey] = thresholdValue;
        ApplyLimits(data, available ? await GetMainchainSettlementLimits(cancellationToken) : null);

        return new SettlementOptionModel
        {
            Type = Type,
            Title = "Bitcoin mainchain",
            Description =
                "Settle Arkade funds to this store's Bitcoin on-chain wallet once the spendable balance reaches the threshold.",
            Available = available,
            Enabled = configuredThreshold is > 0,
            UnavailableReason = unavailableReason,
            SaveCommand = SaveCommand,
            DisableCommand = DisableCommand,
            Data = data
        };
    }

    public async Task<SettlementOptionValidationResult?> ValidateInput(
        StoreData store,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        var (threshold, thresholdError) = ReadThreshold(input);
        if (thresholdError is not null)
            return ValidationError(thresholdError);

        if (threshold is null)
            return null;

        if (!IsAvailable(store, out var unavailableReason))
            return ValidationError(unavailableReason ?? "Settlement option is unavailable.");

        var limits = await GetMainchainSettlementLimits(cancellationToken);
        return ValidateThresholdAgainstLimits(threshold.Value, limits) is { } limitError
            ? ValidationError(limitError)
            : null;
    }

    public ArkadePaymentMethodConfig ApplyInitialSetupDefault(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input)
    {
        if (!IsWalletOwnedByStore(config) || !IsAvailable(store, out _))
            return config;

        var (threshold, _) = ReadThreshold(input);
        return SetConfiguredThreshold(config, threshold);
    }

    public bool HandlesCommand(string command) =>
        command == SaveCommand || command == DisableCommand;

    public async Task<SettlementOptionUpdateResult> HandleCommand(
        string command,
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        if (command == DisableCommand)
        {
            return SettlementOptionUpdateResult.Saved(
                SetConfiguredThreshold(config, null),
                "Mainchain settlement disabled.");
        }

        if (command != SaveCommand)
            return SettlementOptionUpdateResult.Error("Unsupported settlement option command.");

        if (!IsWalletOwnedByStore(config))
            return SettlementOptionUpdateResult.Error(
                "Mainchain settlement requires a wallet owned by this store.");

        var validationResult = await ValidateInput(store, input, cancellationToken);
        if (validationResult is not null)
            return SettlementOptionUpdateResult.Error(validationResult.Message);

        var (threshold, _) = ReadThreshold(input);
        var newConfig = SetConfiguredThreshold(config, threshold);
        var message = threshold is > 0
            ? $"Mainchain settlement threshold set to {threshold.Value:#,0} sats."
            : "Mainchain settlement disabled.";

        return SettlementOptionUpdateResult.Saved(newConfig, message);
    }

    public Task OnSaved(ArkadePaymentMethodConfig config, CancellationToken cancellationToken)
    {
        QueueWallet(config.WalletId);
        return Task.CompletedTask;
    }

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
            await QueueConfiguredWallets(stoppingToken);

            while (await _walletQueue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_walletQueue.Reader.TryRead(out var walletId))
                {
                    _queuedWallets.TryRemove(walletId, out _);
                    await Task.Delay(SettlementCheckDelay, stoppingToken);
                    await ProcessWalletSafely(walletId, stoppingToken);
                }
            }
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
            logger.LogError(ex, "Failed to queue mainchain settlement check for VTXO {OutPoint}", vtxo.OutPoint);
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
        var walletLock = _walletLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            await ProcessWallet(walletId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process mainchain settlement trigger for Arkade wallet {WalletId}", walletId);
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task ProcessWallet(string walletId, CancellationToken cancellationToken)
    {
        var configs = (await GetSettlementConfigs(cancellationToken))
            .Where(config => config.Config.WalletId == walletId)
            .OrderBy(config => config.ThresholdSats)
            .ThenBy(config => config.Store.Id)
            .ToArray();

        if (configs.Length == 0)
            return;

        if (await HasBlockingActiveSwap(walletId, cancellationToken))
            return;

        var availableBalanceSats = await GetAvailableBalanceSats(walletId, cancellationToken);
        var trigger = configs.FirstOrDefault(config =>
            availableBalanceSats >= config.ThresholdSats);

        if (trigger is null)
            return;

        var settlementAmountSats = await GetSettlementTransferAmount(
            walletId,
            availableBalanceSats,
            cancellationToken);
        if (settlementAmountSats is null)
            return;

        var settlementAddress = await GetMainchainSettlementAddress(trigger.Store.Id);
        if (settlementAddress is null)
        {
            logger.LogWarning(
                "Arkade wallet {WalletId} has {AvailableBalanceSats} sats available and settlement threshold {ThresholdSats} sats, but store {StoreId} has no BTC on-chain wallet configured",
                walletId,
                availableBalanceSats,
                trigger.ThresholdSats,
                trigger.Store.Id);
            return;
        }

        try
        {
            var result = await settlementService.InitiateTransfer(
                new SettlementTransferRequest(
                    walletId,
                    settlementAmountSats.Value,
                    SettlementDestination.Bitcoin(settlementAddress)),
                cancellationToken);

            logger.LogInformation(
                "Arkade wallet {WalletId} triggered mainchain settlement {TransferId} to store {StoreId} address {Address}; source={SourceAmountSats} sats destination={DestinationAmountSats} sats fees={FeesPaidSats} sats",
                walletId,
                result.TransferId,
                trigger.Store.Id,
                settlementAddress,
                result.SourceAmountSats,
                result.DestinationAmountSats,
                result.FeesPaidSats);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to settle Arkade wallet {WalletId} balance {AvailableBalanceSats} sats to mainchain for store {StoreId} with amount {SettlementAmountSats} sats",
                walletId,
                availableBalanceSats,
                trigger.Store.Id,
                settlementAmountSats.Value);
        }
    }

    private async Task<long?> GetSettlementTransferAmount(
        string walletId,
        long availableBalanceSats,
        CancellationToken cancellationToken)
    {
        var limits = await GetMainchainSettlementLimits(cancellationToken);
        if (limits is null)
            return availableBalanceSats;

        if (availableBalanceSats < limits.MinAmount)
        {
            logger.LogWarning(
                "Arkade wallet {WalletId} has {AvailableBalanceSats} sats available for mainchain settlement, below Boltz minimum {MinAmountSats} sats",
                walletId,
                availableBalanceSats,
                limits.MinAmount);
            return null;
        }

        if (availableBalanceSats <= limits.MaxAmount)
            return availableBalanceSats;

        logger.LogInformation(
            "Arkade wallet {WalletId} has {AvailableBalanceSats} sats available for mainchain settlement, capping transfer to Boltz maximum {MaxAmountSats} sats",
            walletId,
            availableBalanceSats,
            limits.MaxAmount);
        return limits.MaxAmount;
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

            var thresholdSats = config is null ? null : GetConfiguredThreshold(config);
            if (string.IsNullOrWhiteSpace(config?.WalletId) ||
                !config.GeneratedByStore ||
                thresholdSats is not > 0)
                continue;

            configs.Add(new StoreSettlementConfig(store, config, thresholdSats.Value));
        }

        return configs;
    }

    private async Task<bool> HasBlockingActiveSwap(string walletId, CancellationToken cancellationToken)
    {
        var swaps = await swapStorage.GetSwaps(
            walletIds: [walletId],
            active: true,
            cancellationToken: cancellationToken);

        return swaps.Count > 0;
    }

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

    private async Task<string?> GetMainchainSettlementAddress(string storeId)
    {
        var walletId = new WalletId(storeId, "BTC");
        var address = await walletReceiveService.GetOrGenerate(
            walletId,
            forceGenerate: true);
        var bitcoinAddress = address?.Address;
        if (bitcoinAddress is null)
            return null;

        var addressText = bitcoinAddress.ToString();
        await walletRepository.AddWalletObjectLabels(
            new WalletObjectId(walletId, WalletObjectData.Types.Address, addressText),
            MainchainSettlementAddressLabel);

        return addressText;
    }

    private bool IsAvailable(StoreData store, out string? unavailableReason)
    {
        if (!settlementService.Available)
        {
            unavailableReason = settlementService.UnavailableReason;
            return false;
        }

        if (store.GetDerivationSchemeSettings(paymentMethodHandlerDictionary, "BTC", onlyEnabled: true)
                ?.AccountDerivation is null)
        {
            unavailableReason =
                "Requires a configured Bitcoin on-chain wallet for this store before this settlement option can be enabled.";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    private bool IsAvailable(
        StoreData store,
        ArkadePaymentMethodConfig config,
        out string? unavailableReason)
    {
        if (!IsWalletOwnedByStore(config))
        {
            unavailableReason = "Mainchain settlement requires a wallet owned by this store.";
            return false;
        }

        return IsAvailable(store, out unavailableReason);
    }

    private static bool IsWalletOwnedByStore(ArkadePaymentMethodConfig config) =>
        config.GeneratedByStore || string.IsNullOrWhiteSpace(config.WalletId);

    private static long? NormalizeThreshold(long? thresholdSats) =>
        thresholdSats is > 0 ? thresholdSats : null;

    private static long? GetConfiguredThreshold(ArkadePaymentMethodConfig config)
    {
        var value = config
            .GetSettlementOption(StoreSettlementOption.BitcoinMainchain)
            ?.GetAdditionalData(MainchainSettlementData.ThresholdKey);
        return TryParseThreshold(value, out var threshold)
            ? NormalizeThreshold(threshold)
            : null;
    }

    private static ArkadePaymentMethodConfig SetConfiguredThreshold(
        ArkadePaymentMethodConfig config,
        long? thresholdSats)
    {
        var threshold = NormalizeThreshold(thresholdSats);
        return config.SetSettlementOptionData(
            StoreSettlementOption.BitcoinMainchain,
            threshold is > 0
                ? new JObject
                {
                    [MainchainSettlementData.ThresholdKey] =
                        threshold.Value.ToString(CultureInfo.InvariantCulture)
                }
                : null);
    }

    private static (long? Threshold, string? Error) ReadThreshold(SettlementInput? input)
    {
        var value = input?.Get(MainchainSettlementData.ThresholdKey);
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);

        if (!TryParseThreshold(value, out var threshold))
            return (null, "Settlement threshold must be a whole number of sats.");

        if (threshold < 0)
            return (null, "Settlement threshold cannot be negative.");

        return (NormalizeThreshold(threshold), null);
    }

    private static bool TryParseThreshold(string? value, out long threshold) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out threshold);

    private async Task<BoltzLimits?> GetMainchainSettlementLimits(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arkNetworkConfig.BoltzUri) || boltzLimitsValidator is null)
            return null;

        try
        {
            return await boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch Boltz mainchain settlement limits");
            return null;
        }
    }

    private static void ApplyLimits(JObject data, BoltzLimits? limits)
    {
        if (limits is null)
            return;

        data[MainchainSettlementData.MinSatsKey] = limits.MinAmount;
        data[MainchainSettlementData.MaxSatsKey] = limits.MaxAmount;
    }

    private static string? ValidateThresholdAgainstLimits(long thresholdSats, BoltzLimits? limits)
    {
        // Boltz limits are an optional external dependency. When they cannot be fetched (Boltz
        // briefly unreachable, cold cache) accept the threshold rather than blocking: a hard
        // failure here aborts the whole wallet-creation flow during initial setup, and the
        // settlement path re-checks the live limits at execution time anyway.
        if (limits is null)
            return null;

        if (thresholdSats < limits.MinAmount)
            return $"Mainchain settlement threshold must be at least {limits.MinAmount:#,0} sats.";

        if (thresholdSats > limits.MaxAmount)
            return $"Mainchain settlement threshold cannot exceed {limits.MaxAmount:#,0} sats.";

        return null;
    }

    private static SettlementOptionValidationResult ValidationError(string message) =>
        new(
            SettlementInputName.Field(
                StoreSettlementOption.BitcoinMainchain,
                MainchainSettlementData.ThresholdKey),
            message);

    private record StoreSettlementConfig(StoreData Store, ArkadePaymentMethodConfig Config, long ThresholdSats);
}
