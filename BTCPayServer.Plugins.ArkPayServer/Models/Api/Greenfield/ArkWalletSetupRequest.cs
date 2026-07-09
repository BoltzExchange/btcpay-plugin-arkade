namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to create or import an Arkade wallet for a store.
/// </summary>
public class ArkWalletSetupRequest
{
    /// <summary>
    /// Wallet seed phrase. Supports:
    /// - null/empty: generates a new 12-word BIP-39 mnemonic
    /// - 12 or 24 word BIP-39 mnemonic: imports as HD wallet
    /// </summary>
    public string? Wallet { get; set; }

    /// <summary>
    /// If true, also configures Arkade Lightning (LN) for this store. Default: true.
    /// </summary>
    public bool EnableLightning { get; set; } = true;
}

/// <summary>
/// Response after wallet setup.
/// </summary>
public class ArkWalletSetupResponse
{
    public string WalletId { get; set; } = "";
    public string WalletType { get; set; } = "";
    public bool IsNewWallet { get; set; }
    public bool LightningEnabled { get; set; }

    /// <summary>
    /// The generated mnemonic phrase (only returned for newly generated wallets, not for imports).
    /// </summary>
    public string? Mnemonic { get; set; }
}
