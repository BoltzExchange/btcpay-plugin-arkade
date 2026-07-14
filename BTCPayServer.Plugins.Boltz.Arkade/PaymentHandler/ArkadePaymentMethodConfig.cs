using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
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
