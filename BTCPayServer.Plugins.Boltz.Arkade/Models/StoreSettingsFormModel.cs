using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class StoreSettingsFormModel
{
    [BindNever]
    public Dictionary<StoreSettlementOption, SettlementInput> SettlementInputs { get; set; } = [];
}
