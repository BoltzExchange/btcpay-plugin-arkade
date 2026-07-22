using BTCPayServer.Plugins.Boltz.Arkade.Models;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models.Api.Greenfield;

/// <summary>
/// Threshold-based stablecoin settlement configuration. The same shape is used
/// for writes and reads. Set <see cref="Enabled"/> to false to pause
/// settlement while retaining the destination configuration.
/// </summary>
public class StablecoinSettlementConfigData
{
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }
    public long ThresholdSats { get; set; }
    public string? DestinationChain { get; set; }
    public string? DestinationAddress { get; set; }
    public string? Asset { get; set; }
    public int SlippageBps { get; set; } = UsdSettlementData.DefaultSlippageBps;
}
