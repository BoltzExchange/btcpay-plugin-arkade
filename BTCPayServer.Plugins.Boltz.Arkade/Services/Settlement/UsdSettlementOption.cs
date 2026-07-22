using Boltz.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public sealed class UsdSettlementOption(
    IStablecoinSwapClient stablecoinSwapClient,
    SettlementSchedulerService settlementScheduler) : ISettlementOption
{
    public StoreSettlementOption Type => StoreSettlementOption.Usd;

    public bool Available => stablecoinSwapClient.IsAvailable;

    public string? UnavailableReason => stablecoinSwapClient.UnavailableReason;

    public Task<SettlementOptionModel> CreateViewModel(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new SettlementOptionModel
        {
            Type = Type,
            Title = "Stablecoin",
            Description =
                "Settle Arkade funds to USDT or USDC on the configured destination chain once the spendable balance reaches the threshold.",
            Available = stablecoinSwapClient.IsAvailable,
            Enabled = config.ResolveActiveSettlement() == Type,
            UnavailableReason = stablecoinSwapClient.UnavailableReason,
            Data = UsdSettlementConfiguration.GetViewData(config, input)
        });
    }

    public async Task<SettlementOptionValidationResult?> ValidateInput(
        StoreData store,
        string? walletId,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = UsdSettlementConfiguration.Parse(input);
        if (!result.IsValid)
            return ValidationError(result.InvalidField!, result.Error!);

        if (!stablecoinSwapClient.IsAvailable)
        {
            return ValidationError(
                UsdSettlementData.ThresholdKey,
                stablecoinSwapClient.UnavailableReason ?? "Stablecoin settlement is unavailable.");
        }

        if (result.Config is null)
            return ValidationError(
                UsdSettlementData.ThresholdKey,
                "Enter the required details to enable stablecoin settlement.");

        // Initial setup parses before its wallet is persisted. The controller revalidates after
        // wallet creation; subsequent settings saves arrive here with the existing wallet ID.
        if (string.IsNullOrWhiteSpace(walletId))
            return null;

        try
        {
            var client = await stablecoinSwapClient.GetClient(walletId, cancellationToken);
            var bindingAsset = UsdSettlementConfiguration.ResolveBindingAsset(
                client.DestinationsAccepting(result.Config.DestinationAddress),
                result.Config.DestinationChain,
                result.Config.Asset);

            if (bindingAsset is null)
            {
                return ValidationError(
                    UsdSettlementData.DestinationAddressKey,
                    $"The address is not a supported {result.Config.Asset} destination on {result.Config.DestinationChain}.");
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ValidationError(
                UsdSettlementData.DestinationAddressKey,
                "The stablecoin destination could not be validated. Try again when the stablecoin service is available.");
        }

        return null;
    }

    public Task<SettlementOptionUpdateResult> Save(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken)
    {
        var parsed = UsdSettlementConfiguration.Parse(input).Config!;
        var updated = UsdSettlementConfiguration.Set(config, parsed);
        return Task.FromResult(SettlementOptionUpdateResult.Saved(
            updated,
            parsed.ThresholdSats == 0
                ? $"{parsed.Asset} settlement will start at the swap minimum for {parsed.DestinationChain}."
                : $"{parsed.Asset} settlement threshold set to {parsed.ThresholdSats:#,0} sats for {parsed.DestinationChain}."));
    }

    public Task OnSaved(ArkadePaymentMethodConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settlementScheduler.QueueWallet(config.WalletId);
        return Task.CompletedTask;
    }

    private static SettlementOptionValidationResult ValidationError(string field, string message) =>
        new(SettlementInputName.Field(StoreSettlementOption.Usd, field), message);
}
