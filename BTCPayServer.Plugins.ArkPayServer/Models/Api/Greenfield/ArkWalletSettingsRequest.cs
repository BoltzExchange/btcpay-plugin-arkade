namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to update Arkade wallet settings for a store.
/// All fields are optional — only provided fields are updated.
/// </summary>
public class ArkWalletSettingsRequest
{
    /// <summary>
    /// Enable or disable sub-dust amount payments.
    /// </summary>
    public bool? AllowSubDustAmounts { get; set; }

    /// <summary>
    /// Enable or disable boarding (on-chain to Ark).
    /// </summary>
    public bool? BoardingEnabled { get; set; }

    /// <summary>
    /// Minimum boarding amount in satoshis (must be >= 330).
    /// </summary>
    public long? MinBoardingAmountSats { get; set; }
}
