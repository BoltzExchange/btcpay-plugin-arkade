namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class Send2DestinationViewModel
{
    public int Index { get; set; }

    // Original input
    public string RawDestination { get; set; } = "";

    // Parsed result
    public Send2DestinationType Type { get; set; }
    public string? ResolvedAddress { get; set; }  // Ark address or parsed from BIP21
    public long AmountSats { get; set; }
    public decimal AmountBtc => AmountSats / 100_000_000m;

    // Fee for this destination
    public long FeeSats { get; set; }
    public decimal FeeBtc => FeeSats / 100_000_000m;
    public string? FeeDescription { get; set; }

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
        Send2DestinationType.ArkAddress => "Arkade",
        Send2DestinationType.Bip21Ark => "BIP21 (Arkade)",
        Send2DestinationType.Bip21Lightning => "BIP21 (Lightning)",
        Send2DestinationType.LightningInvoice => "Lightning",
        Send2DestinationType.Lnurl => "LNURL",
        _ => "Unknown"
    };

    public string TypeBadgeClass => Type switch
    {
        Send2DestinationType.ArkAddress => "bg-success",
        Send2DestinationType.Bip21Ark => "bg-success",
        Send2DestinationType.Bip21Lightning => "text-bg-warning",
        Send2DestinationType.LightningInvoice => "text-bg-warning",
        Send2DestinationType.Lnurl => "bg-info",
        _ => "bg-secondary"
    };
}

public enum Send2DestinationType
{
    Unknown,
    ArkAddress,        // Direct ark address (instant, minimal fee)
    Bip21Ark,          // BIP21 with ark= parameter (preferred)
    Bip21Lightning,    // BIP21 with lightning= parameter (swap needed)
    LightningInvoice,  // BOLT11 invoice (swap needed)
    Lnurl              // LNURL-pay (resolve then swap)
}
