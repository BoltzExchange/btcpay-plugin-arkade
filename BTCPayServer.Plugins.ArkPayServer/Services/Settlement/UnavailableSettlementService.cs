using BTCPayServer.Plugins.ArkPayServer.Exceptions;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

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
