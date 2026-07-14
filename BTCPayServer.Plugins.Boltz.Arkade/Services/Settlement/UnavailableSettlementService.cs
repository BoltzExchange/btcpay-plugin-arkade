using BTCPayServer.Plugins.Boltz.Arkade.Exceptions;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public class UnavailableSettlementService(string reason) : ISettlementService
{
    public bool Available => false;
    public string? UnavailableReason => reason;

    public Task<SettlementTransferResult> InitiateTransfer(
        SettlementTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        throw new IncompleteArkadeSetupException(reason);
    }
}
