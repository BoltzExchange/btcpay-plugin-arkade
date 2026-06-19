namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkDashboardWidgetViewModel
{
    public required string StoreId { get; set; }
    public required ArkDashboardPaymentIssue Issue { get; set; }
}

public enum ArkDashboardPaymentIssue
{
    Arkade,
    Lightning,
    ArkadeAndLightning
}
