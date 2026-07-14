using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class StoreSettingsViewModel
{
    public WalletType WalletType { get; set; }

    public bool IsLightningEnabled { get; set; }
    public StoreSettingsFormModel Form { get; set; } = new();
    public bool AllowSubDustAmounts { get; set; }
    public IReadOnlyList<SettlementOptionModel> SettlementOptions { get; set; } = [];

    public string? BoltzUrl { get; set; }
    public bool BoltzConnected { get; set; }
    public string? BoltzError { get; set; }
}
