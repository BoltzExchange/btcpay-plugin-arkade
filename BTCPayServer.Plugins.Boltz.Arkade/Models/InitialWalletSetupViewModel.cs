using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class InitialWalletSetupViewModel
{
    public string? Wallet { get; set; }

    [BindNever]
    public Dictionary<StoreSettlementOption, SettlementInput> SettlementInputs { get; set; } = [];

    public IReadOnlyList<SettlementOptionModel> SettlementOptions { get; set; } = [];
}
