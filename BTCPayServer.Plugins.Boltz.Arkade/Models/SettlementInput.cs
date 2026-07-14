using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class SettlementInput
{
    public JObject Data { get; set; } = new();

    public string? Get(string key) =>
        Data.Value<string>(key);
}

public static class SettlementInputName
{
    public static string Prefix(StoreSettlementOption type) =>
        $"SettlementInputs[{type}].Data";

    public static string Field(StoreSettlementOption type, string key) =>
        $"{Prefix(type)}[{key}]";
}
