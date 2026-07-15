namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

/// <summary>
/// Settlement-side details of a Lightning payment received through a Boltz
/// Lightning↔Arkade reverse swap: the swap id, the Arkade address of the VHTLC
/// contract the funds landed on, and the transaction that funded it.
/// Surfaced as extra columns in the Payments report export.
/// </summary>
public class ArkadeSettlementData
{
    public string? SwapId { get; set; }
    public string? SettlementCurrency { get; set; }
    public string? SettlementAddress { get; set; }
    public string? SettlementTransactionId { get; set; }
}
