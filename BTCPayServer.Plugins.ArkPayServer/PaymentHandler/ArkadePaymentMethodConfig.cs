using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool? WalletBackedUp = null,
    bool AllowSubDustAmounts = false,
    List<StoreSettlementOptionConfig>? SettlementOptions = null)
{
    public StoreSettlementOptionConfig? GetSettlementOption(StoreSettlementOption option) =>
        SettlementOptions?.FirstOrDefault(o => o.Type == StoreSettlementOptionKeys.GetKey(option));

    public ArkadePaymentMethodConfig SetSettlementOptionData(
        StoreSettlementOption option,
        JObject? additionalData)
    {
        var optionKey = StoreSettlementOptionKeys.GetKey(option);
        var settlementOptions = SettlementOptions?
            .Where(o => o.Type != optionKey)
            .ToList() ?? [];

        var normalizedData = NormalizeAdditionalData(additionalData);
        if (normalizedData is not null)
            settlementOptions.Add(new StoreSettlementOptionConfig(optionKey, normalizedData));

        return this with
        {
            SettlementOptions = settlementOptions.Count == 0 ? null : settlementOptions
        };
    }

    private static JObject? NormalizeAdditionalData(JObject? additionalData)
    {
        if (additionalData is null)
            return null;

        var normalizedData = new JObject();
        foreach (var property in additionalData.Properties())
        {
            var value = property.Value.Type == JTokenType.String
                ? property.Value.Value<string>()
                : null;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            normalizedData[property.Name] = value;
        }

        return normalizedData.HasValues ? normalizedData : null;
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
    public const string BitcoinMainchain = "bitcoin-mainchain";

    public static string GetKey(StoreSettlementOption option) =>
        option switch
        {
            StoreSettlementOption.BitcoinMainchain => BitcoinMainchain,
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown settlement option.")
        };
}

public enum StoreSettlementOption
{
    BitcoinMainchain
}
