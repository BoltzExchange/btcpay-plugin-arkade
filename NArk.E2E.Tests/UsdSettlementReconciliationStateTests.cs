using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using Microsoft.Extensions.Logging.Abstractions;
using NArk.Swaps.Models;
using Xunit;

namespace NArk.E2E.Tests;

[Trait("Category", "Unit")]
public class UsdSettlementReconciliationStateTests
{
    [Fact]
    public void CompletedNativeSwap_CompletesWithSettledArkSwap()
    {
        var transfer = Transfer(UsdSettlementState.ArkLegFunded, error: "old error");
        var swap = NativeSwap(new BindingSwapStatus.Completed(), deliveredAmount: 9_750);
        var arkSwap = ArkSwap(ArkSwapStatus.Settled);

        var changed = UsdSettlementReconciliationService.ApplySwapState(transfer, swap, arkSwap);

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.Completed, transfer.State);
        Assert.Equal(9_750, transfer.DeliveredOutputAtomic);
        Assert.Null(transfer.Error);
    }

    // Native completion is the authoritative evidence that stablecoins reached
    // the destination. A missing or lagging Ark row must not reserve the wallet.
    [Theory]
    [InlineData(ArkSwapStatus.Pending, 9_750L, 9_750L)]
    [InlineData(ArkSwapStatus.Unknown, 9_750L, 9_750L)]
    [InlineData(null, null, 10_000L)]
    public void CompletedNativeSwap_WithoutSettledArkSwap_Completes(
        ArkSwapStatus? arkStatus, long? deliveredAmount, long expectedDelivered)
    {
        var transfer = Transfer(UsdSettlementState.ArkLegFunded);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Completed(), deliveredAmount: (ulong?)deliveredAmount),
            arkStatus is null ? null : ArkSwap(arkStatus.Value));

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.Completed, transfer.State);
        Assert.Equal(expectedDelivered, transfer.DeliveredOutputAtomic);
        Assert.Null(transfer.Error);
    }

    // Same transition (Completed native + failed Ark leg → ManualReview);
    // Failed surfaces the recorded reason, Refunded the canonical message.
    [Theory]
    [InlineData(ArkSwapStatus.Failed, "Ark payment rejected", "Ark payment rejected")]
    [InlineData(ArkSwapStatus.Refunded, null, "Native delivery completed but the Ark leg is Refunded.")]
    public void CompletedNativeSwap_WithFailedOrRefundedArkSwap_RequiresManualReview(
        ArkSwapStatus arkStatus, string? failReason, string expectedError)
    {
        var transfer = Transfer(UsdSettlementState.ArkLegFunded);

        UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Completed()),
            ArkSwap(arkStatus, failReason));

        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Equal(expectedError, transfer.Error);
    }

    [Fact]
    public void FailedNativeSwap_RequiresManualReviewAndPreservesReason()
    {
        var transfer = Transfer(UsdSettlementState.ArkLegFunded);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Failed("bridge claim reverted")),
            ArkSwap(ArkSwapStatus.Pending));

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Equal("bridge claim reverted", transfer.Error);
    }

    [Fact]
    public void ExpiredNativeSwap_RequiresManualReviewWithOperatorMessage()
    {
        var transfer = Transfer(UsdSettlementState.PreFunding);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Expired()),
            null);

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Equal("The stablecoin reverse swap expired.", transfer.Error);
    }

    [Fact]
    public void ActiveNativeSwap_WithFailedArkSwap_RequiresManualReview()
    {
        var transfer = Transfer(UsdSettlementState.PreFunding);

        UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Created()),
            ArkSwap(ArkSwapStatus.Failed, "Ark registration failed"));

        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Equal("Ark registration failed", transfer.Error);
    }

    [Fact]
    public void ExpiredNativeSwap_WithRefundedArkSwap_IsRefunded()
    {
        var transfer = Transfer(UsdSettlementState.PreFunding);

        UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Expired()),
            ArkSwap(ArkSwapStatus.Refunded));

        Assert.Equal(UsdSettlementState.Refunded, transfer.State);
        Assert.Equal("The Ark funding leg was refunded.", transfer.Error);
    }

    [Theory]
    [MemberData(nameof(ActiveNativeStates))]
    public void ProgressedNativeSwap_WithRefundedArkSwap_RequiresManualReview(
        BindingSwapStatus status,
        UsdSettlementState _)
    {
        var transfer = Transfer(UsdSettlementState.ArkLegFunded);

        UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(status),
            ArkSwap(ArkSwapStatus.Refunded));

        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Contains("reports Refunded", transfer.Error);
    }

    public static TheoryData<BindingSwapStatus, UsdSettlementState> ActiveNativeStates => new()
    {
        { new BindingSwapStatus.InvoicePaid(), UsdSettlementState.ArkLegFunded },
        { new BindingSwapStatus.TbtcLocked(), UsdSettlementState.TbtcLocked },
        { new BindingSwapStatus.Claiming(), UsdSettlementState.StableClaiming },
        { new BindingSwapStatus.Settling(), UsdSettlementState.BridgeSettling }
    };

    [Theory]
    [MemberData(nameof(ActiveNativeStates))]
    public void ActiveNativeState_AdvancesMonotonically(
        BindingSwapStatus status,
        UsdSettlementState expectedState)
    {
        var transfer = Transfer(UsdSettlementState.PreFunding);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(status),
            null);

        Assert.True(changed);
        Assert.Equal(expectedState, transfer.State);
    }

    [Fact]
    public void CreatedNativeSwap_HasNothingToAdvance()
    {
        // PreFunding is the initial state and FundingStarted is written only by
        // the funding path, so a Created native swap changes nothing.
        var transfer = Transfer(UsdSettlementState.PreFunding);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Created()),
            null);

        Assert.False(changed);
        Assert.Equal(UsdSettlementState.PreFunding, transfer.State);
    }

    [Fact]
    public void StaleNativeState_DoesNotRegressTransfer()
    {
        var transfer = Transfer(UsdSettlementState.BridgeSettling);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.InvoicePaid()),
            null);

        Assert.False(changed);
        Assert.Equal(UsdSettlementState.BridgeSettling, transfer.State);
    }

    public static TheoryData<UsdSettlementState> GuardedStates => new()
    {
        UsdSettlementState.ManualReview,
        UsdSettlementState.Cancelled,
        UsdSettlementState.Refunded,
        UsdSettlementState.Completed
    };

    [Theory]
    [MemberData(nameof(GuardedStates))]
    public void OperatorOwnedAndTerminalStates_AreNotAutomaticallyAdvanced(
        UsdSettlementState guardedState)
    {
        var transfer = Transfer(guardedState, error: "operator decision required");

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Settling()),
            null);

        Assert.False(changed);
        Assert.Equal(guardedState, transfer.State);
        Assert.Equal("operator decision required", transfer.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureSignals_NeverOverwriteManualReview(bool expired)
    {
        // Regression: the pre-refactor Failed/Expired branches assigned
        // transfer.State unconditionally, silently destroying the
        // operator-review flag (and its forensic error text) on an
        // ambiguous-funding row when the native swap later failed or expired
        // — which then unblocked the wallet for a second settlement while the
        // first one's funds were unaccounted for.
        var transfer = Transfer(UsdSettlementState.ManualReview, error: "operator decision required");

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(expired
                ? new BindingSwapStatus.Expired()
                : new BindingSwapStatus.Failed("late native failure")),
            ArkSwap(ArkSwapStatus.Pending));

        Assert.False(changed);
        Assert.Equal(UsdSettlementState.ManualReview, transfer.State);
        Assert.Equal("operator decision required", transfer.Error);
    }

    [Fact]
    public void ManualReview_IsResolvableByDefinitiveExternalEvidence()
    {
        // A parked ManualReview self-resolves once the Ark leg refunds at swap
        // expiry — the only legal automated exits from ManualReview are
        // Completed and Refunded.
        var transfer = Transfer(UsdSettlementState.ManualReview, error: "record mismatch");

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.Expired()),
            ArkSwap(ArkSwapStatus.Refunded));

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.Refunded, transfer.State);
        Assert.Equal("The Ark funding leg was refunded.", transfer.Error);
    }

    [Fact]
    public void ReapplyingSameState_IsIdempotent()
    {
        var transfer = Transfer(UsdSettlementState.TbtcLocked);

        var changed = UsdSettlementReconciliationService.ApplySwapState(
            transfer,
            NativeSwap(new BindingSwapStatus.TbtcLocked()),
            null);

        Assert.False(changed);
        Assert.Equal(UsdSettlementState.TbtcLocked, transfer.State);
    }

    // Evidence persistence doesn't branch on the route, so one direct and one
    // bridged row are representative.
    public static TheoryData<BindingAsset, BindingBridgeKind, string> StablecoinRoutes => new()
    {
        { BindingAsset.Usdt, BindingBridgeKind.Direct, "Arbitrum" },
        { BindingAsset.Usdt0, BindingBridgeKind.Oft, "Solana" }
    };

    [Theory]
    [MemberData(nameof(StablecoinRoutes))]
    public void TransactionEvidence_IsPersistedWithoutAStateTransition(
        BindingAsset asset,
        BindingBridgeKind bridgeKind,
        string destinationChain)
    {
        var transfer = Transfer(UsdSettlementState.BridgeSettling);
        transfer.BridgeKind = null;
        var swap = NativeSwap(
            new BindingSwapStatus.Settling(),
            asset: asset,
            bridgeKind: bridgeKind,
            destinationChain: destinationChain,
            lockupTxId: "tbtc-lockup-tx",
            claimTxHash: "arbitrum-claim-tx",
            bridgeRef: "bridge-reference");

        var changed = UsdSettlementReconciliationService.ApplySwapState(transfer, swap, null);

        Assert.True(changed);
        Assert.Equal(UsdSettlementState.BridgeSettling, transfer.State);
        Assert.Equal(bridgeKind.ToString(), transfer.BridgeKind);
        Assert.Equal("tbtc-lockup-tx", transfer.TbtcLockupTxId);
        Assert.Equal("arbitrum-claim-tx", transfer.ArbitrumClaimTxHash);
        Assert.Equal("bridge-reference", transfer.BridgeRef);

        Assert.False(UsdSettlementReconciliationService.ApplySwapState(transfer, swap, null));
    }

    [Fact]
    public void RecoveryGrace_ExpiresOnlyOncePastTheWindow()
    {
        // The window is measured against UpdatedAt: a live funding pass keeps
        // bumping it, so only rows nothing is driving anymore ever expire. A
        // stale PreFunding row cancels (provably unfunded); a stale
        // FundingStarted row escalates to ManualReview, never Cancelled.
        var transfer = Transfer(UsdSettlementState.FundingStarted);
        var expiry = transfer.UpdatedAt + UsdSettlementReconciliationService.RecoveryGracePeriod;

        Assert.False(UsdSettlementReconciliationService.IsPastRecoveryGrace(
            transfer, expiry - TimeSpan.FromSeconds(1)));
        Assert.True(UsdSettlementReconciliationService.IsPastRecoveryGrace(transfer, expiry));
    }

    [Theory]
    [InlineData(UsdSettlementState.PreFunding, true)]
    [InlineData(UsdSettlementState.FundingStarted, true)]
    [InlineData(UsdSettlementState.ArkLegFunded, true)]
    [InlineData(UsdSettlementState.BridgeSettling, true)]
    [InlineData(UsdSettlementState.ManualReview, true)]
    [InlineData(UsdSettlementState.Completed, false)]
    [InlineData(UsdSettlementState.Refunded, false)]
    [InlineData(UsdSettlementState.Cancelled, false)]
    public void BlockingScope_TracksEveryNonTerminalRow(
        UsdSettlementState state, bool expected)
    {
        // Terminal rows — Cancelled included — are invisible to reconciliation,
        // so a FundingStarted row mislabeled Cancelled would never be looked at
        // again: correctness depends on the cancel guard above.
        Assert.Equal(
            expected,
            UsdSettlementReconciliationService.BlockingScope.Compile()(Transfer(state)));
    }

    public static TheoryData<BindingSwapStatus> AcceptableDegradedStatuses => new()
    {
        new BindingSwapStatus.TbtcLocked(),
        new BindingSwapStatus.Claiming()
    };

    [Theory]
    [MemberData(nameof(AcceptableDegradedStatuses))]
    public async Task DegradedQuote_IsAcceptedInsteadOfParkingTheTransfer(BindingSwapStatus status)
    {
        // The merchant never locked an exchange rate — the threshold triggers
        // a market-rate sweep — so a degraded quote is just the current market:
        // the reconciler accepts it instead of parking the transfer in
        // ManualReview, and the next tick's Advance picks up the result.
        var transfer = MatchedTransfer(UsdSettlementState.TbtcLocked);
        var swap = NativeSwap(status);
        var client = new AcceptRecordingClient();

        var changed = await UsdSettlementReconciliationService.HandleQuoteDegraded(
            transfer,
            swap,
            new BindingEvent.QuoteDegraded(swap, ExpectedUsd: 10_000, QuotedUsd: 9_500),
            client,
            NullLogger.Instance);

        Assert.False(changed);
        Assert.Equal(new[] { "native-1" }, client.AcceptedSwapIds);
        Assert.Equal(UsdSettlementState.TbtcLocked, transfer.State);
        Assert.Null(transfer.Error);
    }

    [Fact]
    public async Task DegradedQuote_OnProgressedSwap_IsNotAccepted()
    {
        // A terminal or already-claiming-complete durable lookup is newer
        // authority than a queued degradation event: nothing to accept.
        var transfer = MatchedTransfer(UsdSettlementState.BridgeSettling);
        var swap = NativeSwap(new BindingSwapStatus.Settling());
        var client = new AcceptRecordingClient();

        var changed = await UsdSettlementReconciliationService.HandleQuoteDegraded(
            transfer,
            swap,
            new BindingEvent.QuoteDegraded(swap, ExpectedUsd: 10_000, QuotedUsd: 9_500),
            client,
            NullLogger.Instance);

        Assert.False(changed);
        Assert.Empty(client.AcceptedSwapIds);
        Assert.Equal(UsdSettlementState.BridgeSettling, transfer.State);
    }

    [Fact]
    public async Task DegradedQuote_ToleratesTheAlreadyProgressedRejection()
    {
        // "generic" is the binding's not-in-TbtcLocked/Claiming rejection: the
        // swap moved on between the durable lookup and the call. Logged, not
        // thrown — the next tick reads whatever the swap became.
        var transfer = MatchedTransfer(UsdSettlementState.TbtcLocked);
        var swap = NativeSwap(new BindingSwapStatus.TbtcLocked());
        var client = new AcceptRecordingClient
        {
            AcceptError = new BindingException.Operation("generic", "swap is not awaiting a quote decision")
        };

        var changed = await UsdSettlementReconciliationService.HandleQuoteDegraded(
            transfer,
            swap,
            new BindingEvent.QuoteDegraded(swap, ExpectedUsd: 10_000, QuotedUsd: 9_500),
            client,
            NullLogger.Instance);

        Assert.False(changed);
        Assert.Equal(new[] { "native-1" }, client.AcceptedSwapIds);
        Assert.Equal(UsdSettlementState.TbtcLocked, transfer.State);
        Assert.Null(transfer.Error);
    }

    private static UsdSettlementTransferEntity Transfer(
        UsdSettlementState state,
        string? error = null) =>
        new()
        {
            Id = "transfer-1",
            StoreId = "store-1",
            WalletId = "wallet-1",
            State = state,
            DestinationNetwork = "Arbitrum",
            DestinationAsset = "USDT",
            DestinationAddress = "0x0123456789abcdef",
            SourceAmountSats = 12_000,
            InvoiceAmountSats = 10_000,
            ExpectedOutputAtomic = 10_000,
            BridgeKind = BindingBridgeKind.Oft.ToString(),
            Error = error,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };

    /// <summary>
    /// A transfer whose durable record matches <see cref="NativeSwap"/> under
    /// <see cref="CompositeUsdSettlementService.ValidateNativeSwap"/>.
    /// </summary>
    private static UsdSettlementTransferEntity MatchedTransfer(UsdSettlementState state)
    {
        var transfer = Transfer(state);
        transfer.RustSwapId = "native-1";
        transfer.Invoice = "lnbc1invoice";
        transfer.SlippageBps = 100;
        return transfer;
    }

    /// <summary>
    /// Minimal IBoltzClient seam for the degraded-quote handler: records
    /// AcceptDegradedQuote calls; every other member is unreachable from the
    /// handler under test.
    /// </summary>
    private sealed class AcceptRecordingClient : IBoltzClient
    {
        public List<string> AcceptedSwapIds { get; } = [];
        public BindingException? AcceptError { get; init; }

        public Task<BindingSwap> AcceptDegradedQuote(string swapId)
        {
            AcceptedSwapIds.Add(swapId);
            if (AcceptError is not null)
                throw AcceptError;
            return Task.FromResult(NativeSwap(new BindingSwapStatus.Claiming()));
        }

        public Task<BindingCreatedSwap> CreateReverseSwap(BindingPreparedSwap prepared) =>
            throw new NotSupportedException();

        public BindingDestination[] DestinationsAccepting(string address) =>
            throw new NotSupportedException();

        public BindingEvent[] DrainEvents() => throw new NotSupportedException();

        public Capabilities GetCapabilities() => throw new NotSupportedException();

        public Task<BindingSwapLimits> GetLimits() => throw new NotSupportedException();

        public Task<BindingSwap?> GetSwap(string swapId) => throw new NotSupportedException();

        public Task<BindingPreparedSwap> PrepareFromSats(
            string destination,
            string chain,
            BindingAsset asset,
            ulong invoiceAmountSats,
            uint? maxSlippageBps) => throw new NotSupportedException();

        public Task<string[]> ResumeSwaps() => throw new NotSupportedException();

        public Task Shutdown() => throw new NotSupportedException();
    }

    private static BindingSwap NativeSwap(
        BindingSwapStatus status,
        ulong expectedOutputAmount = 10_000,
        ulong? deliveredAmount = null,
        BindingAsset asset = BindingAsset.Usdt0,
        BindingBridgeKind bridgeKind = BindingBridgeKind.Oft,
        string destinationChain = "Arbitrum",
        string? lockupTxId = null,
        string? claimTxHash = null,
        string? bridgeRef = null) =>
        new(
            Id: "native-1",
            Status: status,
            BridgeKind: bridgeKind,
            ChainId: 42_161,
            ClaimAddress: "claim-address",
            DestinationAddress: "0x0123456789abcdef",
            DestinationChain: destinationChain,
            Asset: asset,
            RefundAddress: "refund-address",
            Erc20swapAddress: "erc20-swap-address",
            RouterAddress: "router-address",
            Invoice: "lnbc1invoice",
            InvoiceAmountSats: 10_000,
            OnchainAmount: 9_900,
            ExpectedOutputAmount: expectedOutputAmount,
            SlippageBps: 100,
            TimeoutBlockHeight: 1_000,
            LockupTxId: lockupTxId,
            ClaimTxHash: claimTxHash,
            PendingCallId: null,
            DeliveredAmount: deliveredAmount,
            BridgeRef: bridgeRef,
            CreatedAt: 1,
            UpdatedAt: 2);

    private static ArkSwap ArkSwap(ArkSwapStatus status, string? failReason = null) =>
        new(
            SwapId: "ark-1",
            WalletId: "wallet-1",
            SwapType: ArkSwapType.Submarine,
            Invoice: "lnbc1invoice",
            ExpectedAmount: 10_000,
            ContractScript: "contract-script",
            Address: "ark-address",
            Status: status,
            FailReason: failReason,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch,
            Hash: "payment-hash");
}
