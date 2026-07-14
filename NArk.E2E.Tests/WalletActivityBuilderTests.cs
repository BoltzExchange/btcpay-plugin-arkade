using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Services;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using Xunit;

namespace NArk.E2E.Tests;

public class WalletActivityBuilderTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> NoSwaps = new HashSet<string>();

    private static ArkContractEntity Contract(string script, string type, string? source = null) =>
        new(script, ContractActivityState.Active, type, [], "wallet", BaseTime)
        {
            Metadata = source is null ? null : new Dictionary<string, string> { ["Source"] = source }
        };

    private static ArkVtxo Vtxo(
        string script,
        string txId,
        ulong amount,
        string? spentBy = null,
        string? settledBy = null,
        string? arkTxid = null,
        bool unrolled = false,
        bool preconfirmed = false,
        IReadOnlyList<string>? commitmentTxids = null,
        DateTimeOffset? createdAt = null) =>
        new(script, txId, 0, amount, spentBy, settledBy, Swept: false, createdAt ?? BaseTime, null, null,
            Preconfirmed: preconfirmed, Unrolled: unrolled, CommitmentTxids: commitmentTxids, ArkTxid: arkTxid);

    [Fact]
    public void PendingBoardingUtxo_ShowsPendingBoardingEntry()
    {
        var contracts = new[] { Contract("board", ArkBoardingContract.ContractType, "manual") };
        var vtxos = new[] { Vtxo("board", "tx1", 100_000, unrolled: true) };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Boarding", entry.Title);
        Assert.Equal("Pending", entry.BadgeLabel);
        Assert.Equal(0.001m, entry.Amount);
        Assert.False(entry.IsOutgoing);
    }

    [Fact]
    public void SpentBoardingUtxo_ShowsCompletedEntryAndNoSend()
    {
        var contracts = new[] { Contract("board", ArkBoardingContract.ContractType, "manual") };
        var vtxos = new[] { Vtxo("board", "tx1", 100_000, unrolled: true, spentBy: "onchain-spent") };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Boarding", entry.Title);
        Assert.Equal("Completed", entry.BadgeLabel);
    }

    [Fact]
    public void ManualReceive_ShownOnlyForManualPaymentContracts()
    {
        var contracts = new[]
        {
            Contract("manual", ArkPaymentContract.ContractType, "manual"),
            Contract("invoice", ArkPaymentContract.ContractType, "invoice:abc"),
            Contract("change", ArkPaymentContract.ContractType)
        };
        var vtxos = new[]
        {
            Vtxo("manual", "tx1", 25_000),
            Vtxo("invoice", "tx2", 30_000),
            Vtxo("change", "tx3", 35_000)
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Payment received", entry.Title);
        Assert.Equal(0.00025m, entry.Amount);
        Assert.Equal(PaymentStatus.Settled, entry.PaymentStatus);
        Assert.False(entry.IsOutgoing);
    }

    [Fact]
    public void InstantSend_NetsSpentInputsAgainstChange()
    {
        // Newer arkd: SpentBy carries the checkpoint txid; ArkTxid is the spending
        // Arkade txid that links inputs to the change output.
        var contracts = new[] { Contract("pay", ArkPaymentContract.ContractType) };
        var spendDate = BaseTime.AddMinutes(30);
        var vtxos = new[]
        {
            Vtxo("pay", "in1", 100_000, spentBy: "checkpoint1", arkTxid: "send-tx"),
            Vtxo("pay", "in2", 50_000, spentBy: "checkpoint2", arkTxid: "send-tx"),
            Vtxo("pay", "send-tx", 40_000, createdAt: spendDate)
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Payment sent", entry.Title);
        Assert.True(entry.IsOutgoing);
        Assert.Equal(0.0011m, entry.Amount);
        Assert.Equal(spendDate, entry.Date);
    }

    [Fact]
    public void InstantSend_LegacySpentByTxid_NetsAgainstChange()
    {
        // Older arkd reports the spending Arkade txid directly in SpentBy.
        var contracts = new[] { Contract("pay", ArkPaymentContract.ContractType) };
        var vtxos = new[]
        {
            Vtxo("pay", "in1", 100_000, spentBy: "send-tx"),
            Vtxo("pay", "send-tx", 60_000)
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Payment sent", entry.Title);
        Assert.Equal(0.0004m, entry.Amount);
    }

    [Fact]
    public void OwnTransactionOutputs_OnManualContract_AreNotReceives()
    {
        // Change contract derivation recycles input contracts, so the change of a send
        // can land back on the manual contract that was just spent.
        var contracts = new[]
        {
            Contract("pay", ArkPaymentContract.ContractType),
            Contract("manual", ArkPaymentContract.ContractType, "manual")
        };
        var vtxos = new[]
        {
            Vtxo("manual", "in1", 60_000, spentBy: "checkpoint1", arkTxid: "send-tx"),
            Vtxo("manual", "send-tx", 35_000, preconfirmed: true)
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        // The original receive and the send show; the 35k change is not a receive.
        Assert.Equal(2, entries.Count);
        var received = Assert.Single(entries, e => e.Title == "Payment received");
        Assert.Equal(0.0006m, received.Amount);
        var sent = Assert.Single(entries, e => e.Title == "Payment sent");
        Assert.Equal(0.00025m, sent.Amount);
    }

    [Fact]
    public void BatchSettledVtxos_AreRenewals_ShowNoNewEntries()
    {
        // A renewed manual receive keeps its original receive entry, but the renewal
        // itself is neither a send nor a second receive — even when the renewal leaf
        // lands back on the manual contract via contract recycling.
        var contracts = new[] { Contract("manual", ArkPaymentContract.ContractType, "manual") };
        var vtxos = new[]
        {
            Vtxo("manual", "in1", 200_000, spentBy: "forfeit1", settledBy: "commitment"),
            Vtxo("manual", "leaf1", 199_000, commitmentTxids: ["commitment"])
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, NoSwaps);

        var entry = Assert.Single(entries);
        Assert.Equal("Payment received", entry.Title);
        Assert.Equal(0.002m, entry.Amount);
    }

    [Fact]
    public void SwapFundingAndClaimLegs_ShowNoSendEntries()
    {
        var contracts = new[] { Contract("pay", ArkPaymentContract.ContractType) };
        var swapScripts = new HashSet<string> { "htlc" };
        var vtxos = new[]
        {
            // Funding leg: wallet VTXO spent into the swap contract.
            Vtxo("pay", "in1", 100_000, spentBy: "checkpoint1", arkTxid: "fund-tx"),
            Vtxo("htlc", "fund-tx", 99_000, spentBy: "checkpoint2", arkTxid: "claim-tx"),
            // Claim leg: swap contract VTXO spent back to the wallet.
            Vtxo("pay", "claim-tx", 99_000)
        };

        var entries = WalletActivityBuilder.BuildEntries(vtxos, contracts, swapScripts);

        Assert.Empty(entries);
    }
}
