using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class InitialWalletSetupViewModel
{
    public string? Wallet { get; set; }

    [BindNever]
    public Dictionary<StoreSettlementOption, SettlementInput> SettlementInputs { get; set; } = [];

    /// <summary>
    /// The settlement method submitted with the form, so a validation-error
    /// re-render keeps the user's choice pre-checked. Null means the "Off"
    /// choice; distinguish that from an unsubmitted first render via
    /// <see cref="SettlementSelectionSubmitted"/>.
    /// </summary>
    [BindNever]
    public StoreSettlementOption? SelectedSettlement { get; set; }

    /// <summary>
    /// Whether a valid settlement selection accompanied the submitted form. False
    /// on first render, when the view falls back to the first available method.
    /// </summary>
    [BindNever]
    public bool SettlementSelectionSubmitted { get; set; }

    public IReadOnlyList<SettlementOptionModel> SettlementOptions { get; set; } = [];
}
