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
    public JObject? Data { get; set; }

    // Single source of truth for the URL/testid slug of each method, shared by
    // every view that renders settlement options. Distinct from the persisted
    // config key (see StoreSettlementOptionKeys.GetKey): the slug is the shorter
    // identifier the markup and Playwright selectors key off.
    public string Slug => Type switch
    {
        StoreSettlementOption.BitcoinMainchain => "mainchain",
        _ => Type.ToString()
    };
}

public sealed class MainchainSettlementFormViewModel
{
    public required JObject Data { get; init; }
    public required string InputPrefix { get; init; }
    public bool IsInitialSetup { get; init; }
}

public static class MainchainSettlementData
{
    public const string ThresholdKey = "thresholdSats";
    public const string MinSatsKey = "minSats";
    public const string MaxSatsKey = "maxSats";
}
