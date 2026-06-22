using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreSettingsFormModel
{
    public long MinBoardingAmountSats { get; set; }

    [BindNever]
    public Dictionary<StoreSettlementOption, SettlementInput> SettlementInputs { get; set; } = [];
}
