namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public class DestinationSettlementService(
    ArkadeChainSwapSettlementService mainchain,
    CompositeUsdSettlementService usd) : ISettlementService
{
    public bool Available => mainchain.Available || usd.Available;

    public string? UnavailableReason => Available
        ? null
        : usd.UnavailableReason ?? mainchain.UnavailableReason;

    public Task<SettlementTransferResult> InitiateTransfer(
        SettlementTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Destination.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase) ||
            request.Destination.Asset.Equals("USDC", StringComparison.OrdinalIgnoreCase))
        {
            return usd.InitiateTransfer(request, cancellationToken);
        }

        return mainchain.InitiateTransfer(request, cancellationToken);
    }
}
