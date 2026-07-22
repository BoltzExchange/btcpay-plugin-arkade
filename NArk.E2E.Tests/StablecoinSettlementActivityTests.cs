using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Helpers;
using Xunit;

namespace NArk.E2E.Tests;

public class StablecoinSettlementActivityTests
{
    [Theory]
    [InlineData("Oft", "0xguid", "0xsource", "https://layerzeroscan.com/tx/0xsource", "View on LayerZero Scan")]
    [InlineData("Oft", "0xguid", null, "https://layerzeroscan.com/tx/0xguid", "View on LayerZero Scan")]
    [InlineData("Cctp", "3:0xburn", null, "https://ccxp.space/messages?transactionHash=0xBURN&domain=3", "View on ccxp")]
    [InlineData("Direct", null, "0xclaim", "https://arbiscan.io/tx/0xclaim", "View on Arbiscan")]
    public void CreatesBridgeSpecificExplorerLinks(
        string bridgeKind,
        string? bridgeRef,
        string? claimTransactionHash,
        string expectedUrl,
        string expectedLabel)
    {
        var activity = StablecoinSettlementActivity.Create(Transfer(
            UsdSettlementState.Completed,
            bridgeKind,
            bridgeRef,
            claimTransactionHash));

        Assert.Equal(expectedUrl, activity.ExplorerUrl, ignoreCase: true);
        Assert.Equal(expectedLabel, activity.ExplorerLabel);
    }

    [Theory]
    [InlineData(UsdSettlementState.PreFunding, "In progress", "text-bg-warning", true)]
    [InlineData(UsdSettlementState.BridgeSettling, "In progress", "text-bg-warning", true)]
    // ManualReview is pinned: a needs-attention row must never age out of the
    // recent list before an operator acts on it.
    [InlineData(UsdSettlementState.ManualReview, "Needs attention", "text-bg-danger", true)]
    [InlineData(UsdSettlementState.Completed, "Completed", "text-bg-success", false)]
    [InlineData(UsdSettlementState.Refunded, "Refunded", "text-bg-secondary", false)]
    [InlineData(UsdSettlementState.Cancelled, "Cancelled", "text-bg-secondary", false)]
    public void MapsSettlementStateAndOngoingVisibility(
        UsdSettlementState state,
        string expectedLabel,
        string expectedClass,
        bool keepVisible)
    {
        var activity = StablecoinSettlementActivity.Create(Transfer(state));

        Assert.Equal(expectedLabel, activity.BadgeLabel);
        Assert.Equal(expectedClass, activity.BadgeClass);
        Assert.Equal(keepVisible, activity.KeepVisible);
        Assert.Equal(6, activity.AmountDivisibility);
        Assert.Equal(1.220427m, activity.Amount);
    }

    [Fact]
    public void ManualReviewRow_IsPinnedWithErrorDetailAndCancelAction()
    {
        var activity = StablecoinSettlementActivity.Create(Transfer(
            UsdSettlementState.ManualReview,
            error: "Native delivery completed but the Ark leg is Refunded."));

        Assert.True(activity.KeepVisible);
        Assert.Equal("Native delivery completed but the Ark leg is Refunded.", activity.DetailText);
        Assert.True(activity.CanCancel);
        Assert.Equal("transfer", activity.TransferId);
    }

    [Theory]
    [InlineData(UsdSettlementState.BridgeSettling)]
    [InlineData(UsdSettlementState.Cancelled)]
    public void AutomationOwnedRows_OfferNoCancelAction(UsdSettlementState state)
    {
        Assert.False(StablecoinSettlementActivity.Create(Transfer(state)).CanCancel);
    }

    [Fact]
    public void AppendsConsolidatedFeesToAmountSubtextWhenKnown()
    {
        var withFees = Transfer(UsdSettlementState.Completed);
        withFees.StableLegFeeSats = 40;
        withFees.ArkLegFeeSats = 1_210;

        Assert.Equal(
            "2,100 sats from Arkade · 1,250 sats fees",
            StablecoinSettlementActivity.Create(withFees).AmountSubtext);
        Assert.Equal(
            "2,100 sats from Arkade",
            StablecoinSettlementActivity.Create(Transfer(UsdSettlementState.Completed)).AmountSubtext);
    }

    [Theory]
    [InlineData("Oft", null, null)]
    [InlineData("Cctp", "missing-domain", null)]
    [InlineData("Cctp", "3:", null)]
    [InlineData("Direct", null, null)]
    public void OmitsExplorerLinkWhenEvidenceIsMissing(
        string bridgeKind,
        string? bridgeRef,
        string? claimTransactionHash)
    {
        var activity = StablecoinSettlementActivity.Create(Transfer(
            UsdSettlementState.Completed,
            bridgeKind,
            bridgeRef,
            claimTransactionHash));

        Assert.Null(activity.ExplorerUrl);
        Assert.Null(activity.ExplorerLabel);
    }

    private static UsdSettlementTransferEntity Transfer(
        UsdSettlementState state,
        string? bridgeKind = null,
        string? bridgeRef = null,
        string? claimTransactionHash = null,
        string? error = null) =>
        new()
        {
            Id = "transfer",
            StoreId = "store",
            WalletId = "wallet",
            State = state,
            DestinationNetwork = "Solana",
            DestinationAsset = "USDC",
            DestinationAddress = "destination",
            SourceAmountSats = 2_100,
            InvoiceAmountSats = 2_090,
            ExpectedOutputAtomic = 1_250_000,
            DeliveredOutputAtomic = 1_220_427,
            BridgeKind = bridgeKind,
            BridgeRef = bridgeRef,
            ArbitrumClaimTxHash = claimTransactionHash,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
