using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class SettlementOptionModel
{
    public StoreSettlementOption Type { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Available { get; set; }
    public bool Enabled { get; set; }
    public string? UnavailableReason { get; set; }
    public JObject? Data { get; set; }

    // Single source of truth for the URL/testid slug of each method, shared by
    // every view that renders settlement options. Distinct from the persisted
    // config key (see StoreSettlementOptionKeys.GetKey): the slug is the shorter
    // identifier the markup and Playwright selectors key off.
    public string Slug => Type switch
    {
        StoreSettlementOption.BitcoinMainchain => "mainchain",
        StoreSettlementOption.Usd => "usd",
        _ => Type.ToString()
    };
}

public sealed class StablecoinSettlementFormViewModel
{
    public required JObject Data { get; init; }
    public required string InputPrefix { get; init; }
    public bool IsInitialSetup { get; init; }
}

public sealed class MainchainSettlementFormViewModel
{
    public required JObject Data { get; init; }
    public required string InputPrefix { get; init; }
    public bool IsInitialSetup { get; init; }
}

public sealed record UsdSettlementNetwork(
    string InternalName,
    string DisplayName,
    params string[] Assets)
{
    public bool Supports(string asset) =>
        Assets.Contains(asset, StringComparer.Ordinal);
}

public static class MainchainSettlementData
{
    public const string ThresholdKey = "thresholdSats";
    public const string MinSatsKey = "minSats";
    public const string MaxSatsKey = "maxSats";
}

public static class UsdSettlementData
{
    public const string ThresholdKey = "thresholdSats";
    public const string DestinationChainKey = "destinationChain";
    public const string DestinationAddressKey = "destinationAddress";
    public const string AssetKey = "asset";

    public const string UsdtAsset = "USDT";
    public const string UsdcAsset = "USDC";
    public const string DefaultAsset = UsdtAsset;
    public const string DefaultDestinationChain = "Arbitrum One";

    public static readonly IReadOnlyList<string> Assets =
        [UsdtAsset, UsdcAsset];

    // Keep in sync with boltz-web-app's send-enabled USDT0 and USDC chains.
    // TODO: Add Tron when settlement is end-to-end tested and Reown wallet
    // support is implemented for it.
    public static readonly IReadOnlyList<UsdSettlementNetwork> DestinationNetworks =
    [
        new("arbitrum", "Arbitrum One", UsdtAsset, UsdcAsset),
        new("avalanche", "Avalanche", UsdcAsset),
        new("base", "Base", UsdcAsset),
        new("berachain", "Berachain", UsdtAsset),
        new("codex", "Codex", UsdcAsset),
        new("ethereum", "Ethereum", UsdtAsset, UsdcAsset),
        new("hedera", "Hedera", UsdtAsset),
        new("ink", "Ink", UsdtAsset, UsdcAsset),
        new("linea", "Linea", UsdcAsset),
        new("monad", "Monad", UsdcAsset),
        new("optimism", "Optimism", UsdcAsset),
        new("plume", "Plume", UsdcAsset),
        new("polygon", "Polygon PoS", UsdtAsset, UsdcAsset),
        new("sei", "Sei", UsdcAsset),
        new("solana", "Solana", UsdtAsset, UsdcAsset),
        new("sonic", "Sonic", UsdcAsset),
        new("unichain", "Unichain", UsdtAsset, UsdcAsset),
        new("worldchain", "World Chain", UsdcAsset),
        new("xdc", "XDC", UsdcAsset)
    ];

    public static IReadOnlyList<UsdSettlementNetwork> GetDestinationNetworks(string asset) =>
        DestinationNetworks.Where(network => network.Supports(asset)).ToArray();

    public static UsdSettlementNetwork? FindByInternalName(string? internalName) =>
        DestinationNetworks.FirstOrDefault(
            network => network.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    public static UsdSettlementNetwork? FindByDisplayName(string? displayName) =>
        DestinationNetworks.FirstOrDefault(
            network => network.DisplayName.Equals(displayName, StringComparison.Ordinal));
}
