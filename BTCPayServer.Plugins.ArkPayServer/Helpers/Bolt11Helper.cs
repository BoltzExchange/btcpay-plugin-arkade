using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Helpers;

/// <summary>
/// The plugin's single BOLT11 parsing entry point. Invoices must be parsed with the
/// network the Ark operator actually runs on (server info or the injected Lightning
/// network) — never by trying networks in sequence, which misclassifies signet-style
/// networks and multiplies the parse cost.
/// </summary>
public static class Bolt11Helper
{
    public static BOLT11PaymentRequest Parse(string invoice, Network network)
        => BOLT11PaymentRequest.Parse(invoice, network);

    public static BOLT11PaymentRequest? TryParse(string? invoice, Network network)
    {
        if (string.IsNullOrWhiteSpace(invoice))
            return null;
        return BOLT11PaymentRequest.TryParse(invoice, out var bolt11, network) ? bolt11 : null;
    }

    public static long? TryGetAmountSats(string? invoice, Network network)
        => TryParse(invoice, network)?.MinimumAmount is { } amount
            ? (long)amount.ToUnit(LightMoneyUnit.Satoshi)
            : null;
}
