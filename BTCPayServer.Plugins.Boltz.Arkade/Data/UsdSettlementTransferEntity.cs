namespace BTCPayServer.Plugins.Boltz.Arkade.Data;

// Which external resources already exist during PreFunding is carried by
// RustSwapId/NnarkSwapId, not by dedicated states — a PreFunding failure is
// provably unfunded by construction because FundingStarted is durably
// persisted before any broadcast.
public enum UsdSettlementState
{
    PreFunding,
    FundingStarted,
    ArkLegFunded,
    TbtcLocked,
    StableClaiming,
    BridgeSettling,
    Completed,
    Refunded,
    Cancelled,
    ManualReview
}

public static class UsdSettlementStates
{
    public static bool IsTerminal(this UsdSettlementState state) =>
        state is UsdSettlementState.Completed or
            UsdSettlementState.Refunded or
            UsdSettlementState.Cancelled;

    /// <summary>
    /// ManualReview is operator-owned: automation may leave it only on
    /// definitive external evidence (native delivery completed, or the Ark
    /// funding leg refunded) — never on failure or expiry signals, which
    /// would destroy the review flag while funds may be unaccounted for.
    /// </summary>
    public static bool IsOperatorOwned(this UsdSettlementState state) =>
        state == UsdSettlementState.ManualReview;
}

public class UsdSettlementTransferEntity
{
    public required string Id { get; set; }
    public required string StoreId { get; set; }
    public required string WalletId { get; set; }
    public UsdSettlementState State { get; set; }

    // Postgres xmin row-version: DB-maintained optimistic-concurrency token.
    // Detached-attach save paths must restore Property(Xmin).OriginalValue to
    // the value loaded with the entity.
    public uint Xmin { get; set; }

    public required string DestinationNetwork { get; set; }
    public required string DestinationAsset { get; set; }
    public required string DestinationAddress { get; set; }
    public long SourceAmountSats { get; set; }
    public long InvoiceAmountSats { get; set; }
    public long ExpectedOutputAtomic { get; set; }
    public long? DeliveredOutputAtomic { get; set; }
    public int SlippageBps { get; set; }

    public string? RustSwapId { get; set; }
    public string? Invoice { get; set; }
    public string? PaymentHash { get; set; }
    public string? NnarkSwapId { get; set; }
    public string? ArkFundingTxId { get; set; }
    public string? BridgeKind { get; set; }
    public string? TbtcLockupTxId { get; set; }
    public string? ArbitrumClaimTxHash { get; set; }
    public string? BridgeRef { get; set; }

    public long? StableLegFeeSats { get; set; }
    public long? ArkLegFeeSats { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
