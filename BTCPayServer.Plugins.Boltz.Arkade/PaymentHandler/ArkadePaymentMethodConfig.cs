using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool? WalletBackedUp = null,
    bool AllowSubDustAmounts = false,
    List<StoreSettlementOptionConfig>? SettlementOptions = null,
    string? ActiveSettlement = null)
{
    public StoreSettlementOptionConfig? GetSettlementOption(StoreSettlementOption option) =>
        SettlementOptions?.FirstOrDefault(o => o.Type == StoreSettlementOptionKeys.GetKey(option));

    // Settlement methods are mutually exclusive: at most one moves funds at a
    // time. Each method's own config still persists (dormant) so switching
    // methods preserves the merchant's entered values. Only an explicitly
    // stored, recognized key activates a method; anything else is off.
    public StoreSettlementOption? ResolveActiveSettlement() =>
        ActiveSettlement is { Length: > 0 } key &&
        StoreSettlementOptionKeys.TryGetOption(key, out var option)
            ? option
            : null;

    public ArkadePaymentMethodConfig SetActiveSettlement(StoreSettlementOption? option) =>
        this with
        {
            ActiveSettlement = option is { } value
                ? StoreSettlementOptionKeys.GetKey(value)
                : StoreSettlementOptionKeys.None
        };

    // Turning a method off only clears the active selection when that method was
    // the active one, so disabling a dormant method never stops the active one.
    public ArkadePaymentMethodConfig DeactivateSettlement(StoreSettlementOption option) =>
        ResolveActiveSettlement() == option ? SetActiveSettlement(null) : this;

    public ArkadePaymentMethodConfig SetSettlementOptionData(
        StoreSettlementOption option,
        JObject? additionalData)
    {
        var optionKey = StoreSettlementOptionKeys.GetKey(option);
        var settlementOptions = SettlementOptions?
            .Where(o => o.Type != optionKey)
            .ToList() ?? [];

        var prunedData = PruneEmptyValues(additionalData);
        if (prunedData is not null)
            settlementOptions.Add(new StoreSettlementOptionConfig(optionKey, prunedData));

        return this with
        {
            SettlementOptions = settlementOptions.Count == 0 ? null : settlementOptions
        };
    }

    private static JObject? PruneEmptyValues(JObject? additionalData)
    {
        if (additionalData is null)
            return null;

        var prunedData = new JObject();
        foreach (var property in additionalData.Properties())
        {
            if (property.Value.Type == JTokenType.Null)
                continue;

            if (property.Value.Type == JTokenType.String
                && string.IsNullOrWhiteSpace(property.Value.Value<string>()))
                continue;

            prunedData[property.Name] = property.Value;
        }

        return prunedData.HasValues ? prunedData : null;
    }
}

public record StoreSettlementOptionConfig(
    string Type,
    JObject? AdditionalData = null)
{
    public string? GetAdditionalData(string key) =>
        AdditionalData?.Value<string>(key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

public static class StoreSettlementOptionKeys
{
    public const string None = "none";
    public const string BitcoinMainchain = "bitcoin-mainchain";
    public const string Usd = "usd";

    private static readonly Dictionary<StoreSettlementOption, string> Keys = new()
    {
        [StoreSettlementOption.BitcoinMainchain] = BitcoinMainchain,
        [StoreSettlementOption.Usd] = Usd
    };

    public static string GetKey(StoreSettlementOption option) =>
        Keys.TryGetValue(option, out var key)
            ? key
            : throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown settlement option.");

    public static bool TryGetOption(string? key, out StoreSettlementOption option)
    {
        foreach (var (candidate, candidateKey) in Keys)
        {
            if (candidateKey == key)
            {
                option = candidate;
                return true;
            }
        }

        option = default;
        return false;
    }
}

public enum StoreSettlementOption
{
    BitcoinMainchain,
    Usd
}
