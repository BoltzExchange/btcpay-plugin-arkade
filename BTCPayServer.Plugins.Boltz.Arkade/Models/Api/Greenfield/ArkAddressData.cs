namespace BTCPayServer.Plugins.Boltz.Arkade.Models.Api.Greenfield;

/// <summary>
/// Address(es) for receiving Ark payments.
/// </summary>
public class ArkAddressData
{
    /// <summary>
    /// Ark off-chain address (tark1q... / ark1q...).
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Boarding address (P2TR onchain) for depositing BTC into Ark.
    /// Only returned after a boarding address has been generated for wallets that support boarding.
    /// </summary>
    public string? BoardingAddress { get; set; }
}
