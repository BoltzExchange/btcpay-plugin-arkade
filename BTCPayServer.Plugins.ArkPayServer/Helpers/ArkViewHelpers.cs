using NArk.Swaps.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Helpers;

public static class ArkViewHelpers
{
    /// <summary>
    /// The BTCPay server's own network, for address formatting.
    /// </summary>
    public static (Network Network, bool Mainnet) GetNetwork(BTCPayNetworkProvider networkProvider)
    {
        var network = networkProvider.BTC.NBitcoinNetwork;
        return (network, network.ChainName == ChainName.Mainnet);
    }

    public static bool HasArrayFilter(SearchString? search, string type, string? key = null) =>
        search?.ContainsFilter(type) == true &&
        (key is null || search.GetFilterArray(type).Contains(key));

    public static int GetFilterCount(SearchString? search, string filterType) =>
        search?.ContainsFilter(filterType) == true ? search.GetFilterArray(filterType).Length : 0;

    public static string GetSwapTypeLabel(ArkSwapType swapType) =>
        swapType switch
        {
            ArkSwapType.ReverseSubmarine => "Lightning to Arkade",
            ArkSwapType.Submarine => "Arkade to Lightning",
            ArkSwapType.ChainArkToBtc => "Arkade to Bitcoin",
            ArkSwapType.ChainBtcToArk => "Bitcoin to Arkade",
            _ => swapType.ToString()
        };

    public static string GetSwapTypeBadgeClass(ArkSwapType swapType) =>
        swapType switch
        {
            ArkSwapType.ReverseSubmarine => "text-bg-primary",
            ArkSwapType.Submarine => "text-bg-info",
            ArkSwapType.ChainArkToBtc => "text-bg-warning",
            ArkSwapType.ChainBtcToArk => "text-bg-secondary",
            _ => "text-bg-secondary"
        };

    // A failed reverse swap means the Lightning invoice expired unpaid (a funded lockup
    // resolves to Refunded instead), so it renders as neutral "Expired" rather than an
    // alarming failure. True submarine/chain failures stay red.
    public static string GetSwapStatusLabel(ArkSwapStatus status, ArkSwapType swapType) =>
        status == ArkSwapStatus.Failed && swapType == ArkSwapType.ReverseSubmarine
            ? "Expired"
            : status.ToString();

    public static string GetSwapStatusBadgeClass(ArkSwapStatus status, ArkSwapType swapType) =>
        status switch
        {
            ArkSwapStatus.Pending => "text-bg-info",
            ArkSwapStatus.Settled => "text-bg-success",
            ArkSwapStatus.Failed when swapType == ArkSwapType.ReverseSubmarine => "text-bg-secondary",
            ArkSwapStatus.Failed => "text-bg-danger",
            ArkSwapStatus.Refunded => "text-bg-info",
            _ => "text-bg-warning"
        };
}
