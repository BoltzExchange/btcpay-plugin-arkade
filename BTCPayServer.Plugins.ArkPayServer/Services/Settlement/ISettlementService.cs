namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

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
    SettlementDestination Destination);

public record SettlementDestination(
    string Network,
    string Asset,
    string Address)
{
    public static SettlementDestination Bitcoin(string address) =>
        new("bitcoin", "BTC", address);
}

public record SettlementTransferResult(
    string TransferId,
    long SourceAmountSats,
    long DestinationAmountSats,
    long FeesPaidSats);
