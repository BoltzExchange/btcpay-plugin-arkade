using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services;

/// <summary>
/// Builds overview activity entries for wallet events that have no invoice or swap record:
/// boarding deposits, manual receives on wallet-generated addresses, and manual sends
/// reconstructed by netting the wallet's VTXO flows per spending transaction.
/// </summary>
public static class WalletActivityBuilder
{
    /// <summary>Marker written by the boarding-UTXO sync when a boarding UTXO leaves the chain.</summary>
    private const string OnchainSpentMarker = "onchain-spent";
    private const string ManualSource = "manual";

    public static List<RecentPaymentViewModel> BuildEntries(
        IReadOnlyCollection<ArkVtxo> vtxos,
        IReadOnlyCollection<ArkContractEntity> contracts,
        IReadOnlySet<string> swapContractScripts,
        int maxPerCategory = 5)
    {
        var entries = new List<RecentPaymentViewModel>();
        AddBoardingEntries(entries, vtxos, contracts, maxPerCategory);
        AddManualReceiveEntries(entries, vtxos, contracts, maxPerCategory);
        AddManualSendEntries(entries, vtxos, swapContractScripts, maxPerCategory);
        return entries;
    }

    private static void AddBoardingEntries(
        List<RecentPaymentViewModel> entries,
        IReadOnlyCollection<ArkVtxo> vtxos,
        IReadOnlyCollection<ArkContractEntity> contracts,
        int maxPerCategory)
    {
        var boardingScripts = contracts
            .Where(c => c.Type == ArkBoardingContract.ContractType)
            .Select(c => c.Script)
            .ToHashSet();

        foreach (var utxo in vtxos
                     .Where(v => v.Unrolled && boardingScripts.Contains(v.Script))
                     .OrderByDescending(v => v.CreatedAt)
                     .Take(maxPerCategory))
        {
            var pending = !utxo.IsSpent();
            entries.Add(new RecentPaymentViewModel
            {
                Date = utxo.CreatedAt,
                Title = "Boarding",
                Description = "Bitcoin to Arkade",
                Amount = Money.Satoshis((long)utxo.Amount).ToDecimal(MoneyUnit.BTC),
                Currency = "BTC",
                BadgeLabel = pending ? "Pending" : "Completed",
                BadgeClass = pending ? "text-bg-warning" : "text-bg-success",
                AmountSubtext = pending && utxo.IsUnconfirmedOnchain() ? "Waiting for confirmation" : null
            });
        }
    }

    private static void AddManualReceiveEntries(
        List<RecentPaymentViewModel> entries,
        IReadOnlyCollection<ArkVtxo> vtxos,
        IReadOnlyCollection<ArkContractEntity> contracts,
        int maxPerCategory)
    {
        var manualReceiveScripts = contracts
            .Where(c => c.Type == ArkPaymentContract.ContractType &&
                        c.Metadata?.GetValueOrDefault("Source") == ManualSource)
            .Select(c => c.Script)
            .ToHashSet();

        // Outputs of the wallet's own transactions are change or renewals, not receives —
        // change contract derivation recycles input contracts, so change can land back on
        // the very manual contract that was just spent.
        var ownSpendTxids = vtxos
            .Where(v => !v.Unrolled)
            .Select(SpendingTxId)
            .Where(txid => txid is not null && txid != OnchainSpentMarker)
            .ToHashSet();
        var ownSettleCommitments = vtxos
            .Where(v => !string.IsNullOrEmpty(v.SettledByTransactionId))
            .Select(v => v.SettledByTransactionId!)
            .ToHashSet();

        foreach (var vtxo in vtxos
                     .Where(v => !v.Unrolled && manualReceiveScripts.Contains(v.Script))
                     .Where(v => !ownSpendTxids.Contains(v.TransactionId))
                     .Where(v => v.Preconfirmed ||
                                 v.CommitmentTxids?.Any(ownSettleCommitments.Contains) != true)
                     .OrderByDescending(v => v.CreatedAt)
                     .Take(maxPerCategory))
        {
            entries.Add(new RecentPaymentViewModel
            {
                Date = vtxo.CreatedAt,
                Title = "Payment received",
                Description = "Arkade",
                Amount = Money.Satoshis((long)vtxo.Amount).ToDecimal(MoneyUnit.BTC),
                Currency = "BTC",
                PaymentStatus = PaymentStatus.Settled
            });
        }
    }

    private static void AddManualSendEntries(
        List<RecentPaymentViewModel> entries,
        IReadOnlyCollection<ArkVtxo> vtxos,
        IReadOnlySet<string> swapContractScripts,
        int maxPerCategory)
    {
        // What each Arkade transaction created for this wallet (a VTXO's outpoint txid is
        // the txid of the Arkade transaction that created it).
        var createdByTx = new Dictionary<string, CreatedOutputs>();
        foreach (var vtxo in vtxos.Where(v => !v.Unrolled))
        {
            if (!createdByTx.TryGetValue(vtxo.TransactionId, out var created))
                createdByTx[vtxo.TransactionId] = created = new CreatedOutputs();
            created.AmountSats += (long)vtxo.Amount;
            if (created.LatestSeenAt is not { } latest || vtxo.CreatedAt > latest)
                created.LatestSeenAt = vtxo.CreatedAt;
            created.TouchesSwapContract |= swapContractScripts.Contains(vtxo.Script);
        }

        var sends = new List<RecentPaymentViewModel>();
        foreach (var group in vtxos
                     .Where(v => v is { Unrolled: false, Swept: false })
                     // Batch-settled VTXOs are renewals: the batch returns the funds to the
                     // wallet minus fees, so they are never manual payments.
                     .Where(v => string.IsNullOrEmpty(v.SettledByTransactionId))
                     .Select(v => (Vtxo: v, SpendTxId: SpendingTxId(v)))
                     .Where(t => t.SpendTxId is not null && t.SpendTxId != OnchainSpentMarker)
                     .GroupBy(t => t.SpendTxId!))
        {
            // Swap funding, claim, and refund legs are already represented by swap entries.
            if (group.Any(t => swapContractScripts.Contains(t.Vtxo.Script)))
                continue;
            createdByTx.TryGetValue(group.Key, out var created);
            if (created?.TouchesSwapContract == true)
                continue;

            var netSats = group.Sum(t => (long)t.Vtxo.Amount) - (created?.AmountSats ?? 0);
            if (netSats <= 0)
                continue; // internal movement: self-transfer or change-only

            sends.Add(new RecentPaymentViewModel
            {
                Date = created?.LatestSeenAt ?? group.Max(t => t.Vtxo.CreatedAt),
                Title = "Payment sent",
                Description = "Arkade",
                Amount = Money.Satoshis(netSats).ToDecimal(MoneyUnit.BTC),
                Currency = "BTC",
                IsOutgoing = true,
                PaymentStatus = PaymentStatus.Settled
            });
        }

        entries.AddRange(sends.OrderByDescending(e => e.Date).Take(maxPerCategory));
    }

    /// <summary>
    /// The Arkade txid that spent this VTXO, or null when unspent. Newer arkd reports it
    /// as ArkTxid and uses SpentBy for the checkpoint txid; older arkd puts it in SpentBy.
    /// </summary>
    private static string? SpendingTxId(ArkVtxo vtxo) =>
        string.IsNullOrEmpty(vtxo.SpentByTransactionId)
            ? null
            : string.IsNullOrEmpty(vtxo.ArkTxid) ? vtxo.SpentByTransactionId : vtxo.ArkTxid;

    private sealed class CreatedOutputs
    {
        public long AmountSats;
        public DateTimeOffset? LatestSeenAt;
        public bool TouchesSwapContract;
    }
}
