namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class SendDestinationViewModel
{
    // Original input
    public string RawDestination { get; set; } = "";

    // Parsed result
    public SendDestinationType Type { get; set; }
    public string? ResolvedAddress { get; set; }  // Ark address or parsed from BIP21
    public long AmountSats { get; set; }
    public decimal AmountBtc => AmountSats / 100_000_000m;

    // Payout tracking (when initiated from payout handler)
    public string? PayoutId { get; set; }

    // LNURL metadata (populated on resolution)
    public long LnurlMinSats { get; set; }
    public long LnurlMaxSats { get; set; }

    // Validation
    public bool IsValid { get; set; }
    public string? Error { get; set; }

    // Display helpers
    public string TypeBadge => Type switch
    {
        SendDestinationType.ArkAddress => "Arkade",
        SendDestinationType.Bip21Ark => "BIP21 (Arkade)",
        SendDestinationType.Bip21Lightning => "BIP21 (Lightning)",
        SendDestinationType.LightningInvoice => "Lightning",
        SendDestinationType.Lnurl => "LNURL",
        _ => "Unknown"
    };

    public string TypeBadgeClass => Type switch
    {
        SendDestinationType.ArkAddress => "bg-success",
        SendDestinationType.Bip21Ark => "bg-success",
        SendDestinationType.Bip21Lightning => "text-bg-warning",
        SendDestinationType.LightningInvoice => "text-bg-warning",
        SendDestinationType.Lnurl => "bg-info",
        _ => "bg-secondary"
    };
}

public enum SendDestinationType
{
    Unknown,
    ArkAddress,        // Direct ark address (instant, minimal fee)
    Bip21Ark,          // BIP21 with ark= parameter (preferred)
    Bip21Lightning,    // BIP21 with lightning= parameter (swap needed)
    LightningInvoice,  // BOLT11 invoice (swap needed)
    Lnurl              // LNURL-pay (resolve then swap)
}
