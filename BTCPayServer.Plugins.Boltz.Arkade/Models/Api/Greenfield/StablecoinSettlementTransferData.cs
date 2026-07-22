namespace BTCPayServer.Plugins.Boltz.Arkade.Models.Api.Greenfield;

/// <summary>
/// Public, store-scoped status information for a stablecoin settlement transfer.
/// </summary>
public class StablecoinSettlementTransferData
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string DestinationAsset { get; set; } = "";
    public string DestinationChain { get; set; } = "";
    public string DestinationAddress { get; set; } = "";
    public long SourceAmountSats { get; set; }
    public long ExpectedOutputAtomic { get; set; }
    public long? DeliveredOutputAtomic { get; set; }

    /// <summary>
    /// Consolidated fees across both legs (stable + Ark); null until a quote
    /// has been prepared.
    /// </summary>
    public long? FeesPaidSats { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Message { get; set; }
}
