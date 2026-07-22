using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

namespace BTCPayServer.Plugins.Boltz.Arkade.Helpers;

internal static class StablecoinSettlementActivity
{
    private const decimal StablecoinAtomicUnits = 1_000_000m;

    internal static RecentPaymentViewModel Create(UsdSettlementTransferEntity transfer)
    {
        var (badgeLabel, badgeClass) = Badge(transfer.State);
        var (explorerUrl, explorerLabel) = Explorer(transfer);
        var outputAtomic = transfer.DeliveredOutputAtomic ?? transfer.ExpectedOutputAtomic;
        var feeSats = (transfer.StableLegFeeSats ?? 0) + (transfer.ArkLegFeeSats ?? 0);
        var asset = UsdSettlementConfiguration.CanonicalizeAsset(transfer.DestinationAsset);

        return new RecentPaymentViewModel
        {
            Date = transfer.UpdatedAt,
            Title = "Stablecoin settlement",
            Description = $"{asset} to {transfer.DestinationNetwork}",
            Amount = outputAtomic / StablecoinAtomicUnits,
            Currency = asset,
            AmountDivisibility = 6,
            AmountPrefix = "",
            AmountSubtext = feeSats > 0
                ? $"{transfer.SourceAmountSats:N0} sats from Arkade · {feeSats:N0} sats fees"
                : $"{transfer.SourceAmountSats:N0} sats from Arkade",
            AmountSubtextSensitive = true,
            ShowAmount = outputAtomic > 0,
            BadgeLabel = badgeLabel,
            BadgeClass = badgeClass,
            // Ongoing rows are pinned so progress stays watchable; ManualReview
            // rows are pinned because they demand operator action and must
            // never age out of the recent list.
            KeepVisible = IsOngoing(transfer.State) ||
                          transfer.State == UsdSettlementState.ManualReview,
            ExplorerUrl = explorerUrl,
            ExplorerLabel = explorerLabel,
            DetailText = transfer.Error,
            TransferId = transfer.Id,
            CanCancel = transfer.State.IsOperatorOwned()
        };
    }

    // Single source for state classification: the overview queries and the
    // activity rows must always agree on what counts as ongoing/terminal.
    internal static readonly UsdSettlementState[] OngoingStates =
    [
        UsdSettlementState.PreFunding,
        UsdSettlementState.FundingStarted,
        UsdSettlementState.ArkLegFunded,
        UsdSettlementState.TbtcLocked,
        UsdSettlementState.StableClaiming,
        UsdSettlementState.BridgeSettling
    ];

    internal static readonly UsdSettlementState[] TerminalStates =
    [
        UsdSettlementState.Completed,
        UsdSettlementState.Refunded,
        UsdSettlementState.Cancelled
    ];

    private static bool IsOngoing(UsdSettlementState state) =>
        OngoingStates.Contains(state);

    private static (string Label, string CssClass) Badge(UsdSettlementState state) => state switch
    {
        UsdSettlementState.Completed => ("Completed", "text-bg-success"),
        UsdSettlementState.Refunded => ("Refunded", "text-bg-secondary"),
        UsdSettlementState.Cancelled => ("Cancelled", "text-bg-secondary"),
        UsdSettlementState.ManualReview => ("Needs attention", "text-bg-danger"),
        _ => ("In progress", "text-bg-warning")
    };

    private static (string? Url, string? Label) Explorer(UsdSettlementTransferEntity transfer)
    {
        if (!Enum.TryParse<BindingBridgeKind>(transfer.BridgeKind, ignoreCase: true, out var bridgeKind))
            return (null, null);

        switch (bridgeKind)
        {
            case BindingBridgeKind.Oft:
                var oftTransactionHash = transfer.ArbitrumClaimTxHash ?? transfer.BridgeRef;
                if (!string.IsNullOrWhiteSpace(oftTransactionHash))
                {
                    return ($"https://layerzeroscan.com/tx/{Uri.EscapeDataString(oftTransactionHash)}",
                        "View on LayerZero Scan");
                }
                break;

            case BindingBridgeKind.Cctp
                when TryParseCctpReference(transfer.BridgeRef, out var domain, out var transactionHash):
                return ($"https://ccxp.space/messages?transactionHash={Uri.EscapeDataString(transactionHash)}&domain={domain}",
                    "View on ccxp");

            case BindingBridgeKind.Direct
                when !string.IsNullOrWhiteSpace(transfer.ArbitrumClaimTxHash):
                return ($"https://arbiscan.io/tx/{Uri.EscapeDataString(transfer.ArbitrumClaimTxHash)}",
                    "View on Arbiscan");
        }

        return (null, null);
    }

    private static bool TryParseCctpReference(string? value, out uint domain, out string transactionHash)
    {
        domain = 0;
        transactionHash = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            !uint.TryParse(value.AsSpan(0, separator), out domain))
            return false;

        transactionHash = value[(separator + 1)..];
        return true;
    }
}
