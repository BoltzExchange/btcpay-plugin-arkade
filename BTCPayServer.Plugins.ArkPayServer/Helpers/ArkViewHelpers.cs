using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Helpers;

public static class ArkViewHelpers
{
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
}
