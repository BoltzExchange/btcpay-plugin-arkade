using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>How the initial setup wallet input should be interpreted.</summary>
public enum WalletSetupMode
{
    /// <summary>Infer wallet type from the posted value.</summary>
    Auto = 0
}

public class InitialWalletSetupViewModel
{
    public string? Wallet { get; set; }

    [BindNever]
    public Dictionary<StoreSettlementOption, SettlementInput> SettlementInputs { get; set; } = [];

    public IReadOnlyList<SettlementOptionModel> SettlementOptions { get; set; } = [];

    /// <summary>Interpretation mode for <see cref="Wallet"/>.</summary>
    public WalletSetupMode Mode { get; set; } = WalletSetupMode.Auto;
}
