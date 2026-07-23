using System.Globalization;
using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public sealed record UsdSettlementConfig(
    long ThresholdSats,
    string DestinationChain,
    string DestinationAddress,
    string Asset);

public sealed record UsdSettlementConfigResult(
    UsdSettlementConfig? Config,
    string? InvalidField = null,
    string? Error = null)
{
    public bool IsValid => Error is null;
}

public static class UsdSettlementConfiguration
{
    public static UsdSettlementConfig? Get(ArkadePaymentMethodConfig config)
    {
        var data = config.GetSettlementOption(StoreSettlementOption.Usd)?.AdditionalData;
        if (data is null)
            return null;

        var result = Parse(new SettlementInput { Data = (JObject)data.DeepClone() });
        return result.IsValid ? result.Config : null;
    }

    public static JObject GetViewData(
        ArkadePaymentMethodConfig config,
        SettlementInput? input)
    {
        if (input is not null)
        {
            var inputData = (JObject)input.Data.DeepClone();
            SetDefault(inputData, UsdSettlementData.AssetKey, UsdSettlementData.DefaultAsset);
            if (TryCanonicalizeAsset(
                    inputData.Value<string>(UsdSettlementData.AssetKey),
                    out var canonicalAsset))
            {
                inputData[UsdSettlementData.AssetKey] = canonicalAsset;
            }
            return inputData;
        }

        return Get(config) is { } configured
            ? ToData(configured)
            : new JObject
            {
                [UsdSettlementData.AssetKey] = UsdSettlementData.DefaultAsset
            };
    }

    public static UsdSettlementConfigResult Parse(SettlementInput? input)
    {
        var thresholdValue = Read(input, UsdSettlementData.ThresholdKey);
        if (string.IsNullOrWhiteSpace(thresholdValue) &&
            string.IsNullOrWhiteSpace(Read(input, UsdSettlementData.DestinationAddressKey)))
            return new UsdSettlementConfigResult(null);

        if (!long.TryParse(
                string.IsNullOrWhiteSpace(thresholdValue) ? "0" : thresholdValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var thresholdSats))
        {
            return Invalid(
                UsdSettlementData.ThresholdKey,
                "Stablecoin settlement threshold must be a whole number of sats.");
        }

        if (thresholdSats < 0)
        {
            return Invalid(
                UsdSettlementData.ThresholdKey,
                "Stablecoin settlement threshold cannot be negative.");
        }

        var destinationChain = Read(input, UsdSettlementData.DestinationChainKey);
        if (string.IsNullOrWhiteSpace(destinationChain))
        {
            return Invalid(
                UsdSettlementData.DestinationChainKey,
                "A destination chain is required for stablecoin settlement.");
        }

        if (!UsdSettlementData.DestinationChains.Contains(destinationChain.Trim(), StringComparer.Ordinal))
        {
            return Invalid(
                UsdSettlementData.DestinationChainKey,
                $"Destination chain must be one of {string.Join(", ", UsdSettlementData.DestinationChains)}.");
        }

        var destinationAddress = Read(input, UsdSettlementData.DestinationAddressKey);
        if (string.IsNullOrWhiteSpace(destinationAddress))
        {
            return Invalid(
                UsdSettlementData.DestinationAddressKey,
                "A destination address is required for stablecoin settlement.");
        }

        var asset = Read(input, UsdSettlementData.AssetKey);
        if (!TryCanonicalizeAsset(asset, out var canonicalAsset))
        {
            return Invalid(
                UsdSettlementData.AssetKey,
                $"Settlement asset must be one of {string.Join(", ", UsdSettlementData.Assets)}.");
        }

        return new UsdSettlementConfigResult(
            new UsdSettlementConfig(
                thresholdSats,
                destinationChain.Trim(),
                destinationAddress.Trim(),
                canonicalAsset));
    }

    public static ArkadePaymentMethodConfig Set(
        ArkadePaymentMethodConfig paymentMethodConfig,
        UsdSettlementConfig? settlementConfig) =>
        paymentMethodConfig.SetSettlementOptionData(
            StoreSettlementOption.Usd,
            settlementConfig is null ? null : ToData(settlementConfig));

    public static JObject ToData(UsdSettlementConfig config) =>
        new()
        {
            [UsdSettlementData.ThresholdKey] =
                config.ThresholdSats.ToString(CultureInfo.InvariantCulture),
            [UsdSettlementData.DestinationChainKey] = config.DestinationChain,
            [UsdSettlementData.DestinationAddressKey] = config.DestinationAddress,
            [UsdSettlementData.AssetKey] = CanonicalizeAsset(config.Asset)
        };

    public static BindingAsset? ResolveBindingAsset(
        IEnumerable<BindingDestination> destinations,
        string chain,
        string asset)
    {
        var canonicalAsset = CanonicalizeAsset(asset);
        var matchingAssets = destinations
            .Where(destination => destination.ChainLabel.Equals(chain, StringComparison.Ordinal))
            .Select(destination => destination.Asset)
            .ToHashSet();

        return canonicalAsset switch
        {
            UsdSettlementData.UsdtAsset when matchingAssets.Contains(BindingAsset.Usdt) => BindingAsset.Usdt,
            UsdSettlementData.UsdtAsset when matchingAssets.Contains(BindingAsset.Usdt0) => BindingAsset.Usdt0,
            UsdSettlementData.UsdcAsset when matchingAssets.Contains(BindingAsset.Usdc) => BindingAsset.Usdc,
            _ => null
        };
    }

    public static bool MatchesBindingAsset(string asset, BindingAsset bindingAsset) =>
        CanonicalizeAsset(asset) switch
        {
            UsdSettlementData.UsdtAsset => bindingAsset is BindingAsset.Usdt or BindingAsset.Usdt0,
            UsdSettlementData.UsdcAsset => bindingAsset == BindingAsset.Usdc,
            _ => throw new ArgumentException("Unsupported stablecoin settlement asset.", nameof(asset))
        };

    public static string CanonicalizeAsset(string? asset) =>
        TryCanonicalizeAsset(asset, out var canonicalAsset)
            ? canonicalAsset
            : throw new ArgumentException("Unsupported stablecoin settlement asset.", nameof(asset));

    private static bool TryCanonicalizeAsset(string? asset, out string canonicalAsset)
    {
        canonicalAsset = asset?.Trim().ToUpperInvariant() switch
        {
            UsdSettlementData.UsdtAsset => UsdSettlementData.UsdtAsset,
            UsdSettlementData.UsdcAsset => UsdSettlementData.UsdcAsset,
            _ => string.Empty
        };
        return canonicalAsset.Length > 0;
    }

    private static string? Read(SettlementInput? input, string key) =>
        input?.Get(key)?.Trim();

    private static UsdSettlementConfigResult Invalid(string field, string error) =>
        new(null, field, error);

    private static void SetDefault(JObject data, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(data.Value<string>(key)))
            data[key] = value;
    }
}
