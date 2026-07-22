namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public interface ISettlementService
{
    bool Available { get; }
    string? UnavailableReason { get; }

    Task<SettlementTransferResult> InitiateTransfer(
        SettlementTransferRequest request,
        CancellationToken cancellationToken = default);
}

public record SettlementTransferRequest(
    string WalletId,
    long AmountSats,
    SettlementDestination Destination,
    string? StoreId = null,
    uint? MaxSlippageBps = null);

public record SettlementDestination(
    string Network,
    string Asset,
    string Address)
{
    public static SettlementDestination Bitcoin(string address) =>
        new("bitcoin", "BTC", address);

    public static SettlementDestination Stablecoin(string chain, string asset, string address) =>
        new(chain, asset.Trim().ToUpperInvariant(), address);
}

// DestinationAtomicAmount carries stablecoin outputs (atomic units of the
// destination asset); DestinationAmountSats stays sats for mainchain results.
public record SettlementTransferResult(
    string TransferId,
    long SourceAmountSats,
    long DestinationAmountSats,
    long FeesPaidSats,
    long? DestinationAtomicAmount = null);
