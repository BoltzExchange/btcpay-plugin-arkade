using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class SettlementOptionModel
{
    public StoreSettlementOption Type { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Available { get; set; }
    public bool Enabled { get; set; }
    public string? UnavailableReason { get; set; }
    public string SaveCommand { get; set; } = "";
    public string DisableCommand { get; set; } = "";
    public JObject? Data { get; set; }
}

public static class MainchainSettlementData
{
    public const string ThresholdKey = "thresholdSats";
    public const string MinSatsKey = "minSats";
    public const string MaxSatsKey = "maxSats";
}
