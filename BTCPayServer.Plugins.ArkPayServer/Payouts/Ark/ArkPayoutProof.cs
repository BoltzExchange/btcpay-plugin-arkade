using System.Text.Json.Serialization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkPayoutProof: IPayoutProof
{
    public const string Type = "PayoutProofArk";
    
    [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
    public uint256 TransactionId { get; set; } = uint256.Zero;

    public string? TransferId { get; set; }
    
    [JsonIgnore]
    public string Id => TransferId ?? TransactionId.ToString();

    /// <summary>
    /// The payout state this proof implies. A real on-ledger transaction id means the funds
    /// were delivered (<see cref="PayoutState.Completed"/>). A chain-swap transfer id means the
    /// transfer was only initiated and is still settling, so the payout is
    /// <see cref="PayoutState.InProgress"/> until the swap settles — it must not be marked paid
    /// yet. A proof carrying neither yields <c>null</c>, leaving the payout in its current state.
    /// </summary>
    [JsonIgnore]
    public PayoutState? ResolvedPayoutState =>
        TransactionId != uint256.Zero ? PayoutState.Completed
        : !string.IsNullOrEmpty(TransferId) ? PayoutState.InProgress
        : null;

    public string ProofType => Type;
    [JsonIgnore]
    public string? Link { get; set; }

    public static ArkPayoutProof FromSpendResult(string? spendResult)
    {
        var proof = new ArkPayoutProof();

        if (string.IsNullOrWhiteSpace(spendResult))
            return proof;

        if (uint256.TryParse(spendResult, out var txId))
            proof.TransactionId = txId;
        else
            proof.TransferId = spendResult;

        return proof;
    }
}
