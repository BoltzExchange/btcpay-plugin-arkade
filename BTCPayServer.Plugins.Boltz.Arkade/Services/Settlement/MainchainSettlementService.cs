using System.Collections.Concurrent;
using System.Globalization;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz;
using NArk.Hosting;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public class MainchainSettlementService(
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    WalletRepository walletRepository,
    WalletReceiveService walletReceiveService,
    ISettlementService settlementService,
    Lazy<SettlementSchedulerService> settlementScheduler,
    BoltzLimitsValidator? boltzLimitsValidator,
    ArkNetworkConfig arkNetworkConfig,
    ILogger<MainchainSettlementService> logger) : ISettlementOption
{
    private const string MainchainSettlementAddressLabel = "Arkade settlement swap";

    // Settlement address reserved per store, kept until a swap to it is actually
    // created. Without this every trigger event during a Boltz outage would
    // force-generate (and burn) a fresh on-chain address, racing towards the
    // NBXplorer gap limit.
    private readonly ConcurrentDictionary<string, string> _pendingSettlementAddresses = new();

    public StoreSettlementOption Type => StoreSettlementOption.BitcoinMainchain;

    public async Task<SettlementOptionModel> CreateViewModel(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        var available = IsAvailable(store, out var unavailableReason);
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
            Enabled = config.ResolveActiveSettlement() == Type,
            UnavailableReason = unavailableReason,
            Data = data
        };
    }

    public async Task<SettlementOptionValidationResult?> ValidateInput(
        StoreData store,
        string? walletId,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable(store, out var unavailableReason))
            return ValidationError(unavailableReason ?? "Settlement option is unavailable.");

        var (threshold, thresholdError) = ReadThreshold(input);
        if (thresholdError is not null)
            return ValidationError(thresholdError);

        if (threshold is null)
            return ValidationError("Enter a settlement threshold to enable Bitcoin mainchain settlement.");

        var limits = await GetMainchainSettlementLimits(cancellationToken);
        return ValidateThresholdAgainstLimits(threshold.Value, limits) is { } limitError
            ? ValidationError(limitError)
            : null;
    }

    public Task<SettlementOptionUpdateResult> Save(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        var (threshold, _) = ReadThreshold(input);
        var newConfig = SetConfiguredThreshold(config, threshold);
        return Task.FromResult(SettlementOptionUpdateResult.Saved(
            newConfig,
            $"Mainchain settlement threshold set to {threshold.GetValueOrDefault():#,0} sats."));
    }

    public Task OnSaved(ArkadePaymentMethodConfig config, CancellationToken cancellationToken)
    {
        settlementScheduler.Value.QueueWallet(config.WalletId);
        return Task.CompletedTask;
    }

    internal async Task<long?> GetSettlementTransferAmount(
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

    internal async Task SettleWallet(
        string walletId,
        string storeId,
        long thresholdSats,
        long settlementAmountSats,
        long availableBalanceSats,
        CancellationToken cancellationToken)
    {
        var settlementAddress = await GetMainchainSettlementAddress(storeId);
        if (settlementAddress is null)
        {
            logger.LogWarning(
                "Arkade wallet {WalletId} has {AvailableBalanceSats} sats available and settlement threshold {ThresholdSats} sats, but store {StoreId} has no BTC on-chain wallet configured",
                walletId,
                availableBalanceSats,
                thresholdSats,
                storeId);
            return;
        }

        try
        {
            var result = await settlementService.InitiateTransfer(
                new SettlementTransferRequest(
                    walletId,
                    settlementAmountSats,
                    SettlementDestination.Bitcoin(settlementAddress)),
                cancellationToken);

            // The swap now pays to this address; the next settlement needs a fresh one.
            _pendingSettlementAddresses.TryRemove(storeId, out _);

            logger.LogInformation(
                "Arkade wallet {WalletId} triggered mainchain settlement {TransferId} to store {StoreId} address {Address}; source={SourceAmountSats} sats destination={DestinationAmountSats} sats fees={FeesPaidSats} sats",
                walletId,
                result.TransferId,
                storeId,
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
                storeId,
                settlementAmountSats);
        }
    }

    private async Task<string?> GetMainchainSettlementAddress(string storeId)
    {
        // Reuse the address reserved by a previous attempt whose transfer never
        // happened (e.g. Boltz unreachable); only force-generate once the last
        // one was consumed by a successful settlement.
        if (_pendingSettlementAddresses.TryGetValue(storeId, out var pending))
            return pending;

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

        _pendingSettlementAddresses[storeId] = addressText;
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

    private static long? NormalizeThreshold(long? thresholdSats) =>
        thresholdSats is > 0 ? thresholdSats : null;

    internal static long? GetConfiguredThreshold(ArkadePaymentMethodConfig config)
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
}
