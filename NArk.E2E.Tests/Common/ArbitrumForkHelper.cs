using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Minimal JSON-RPC helper for the regtest stack's anvil-arb service — a fork
/// of Arbitrum One (chain id 42161) whose state contains the real TBTC/USDT
/// tokens, Uniswap pools, and Boltz contracts the native stablecoin client is
/// hardwired to. Mirrors boltz-web-app's
/// <c>packages/boltz-swaps/integration/arbitrum.ts</c>: whale funding via
/// anvil impersonation and plain <c>eth_call</c> balance assertions.
/// </summary>
public static class ArbitrumForkHelper
{
    public const string RpcUrl = "http://localhost:18545";

    public const long ArbitrumChainId = 42161;

    /// <summary>Real Arbitrum One TBTC; resolved by the fork.</summary>
    public const string TbtcTokenAddress = "0x6c84a8f1c29108F47a79964b5Fe888D4f4D0dE40";

    /// <summary>Real Arbitrum One USDT (6 decimals); resolved by the fork.</summary>
    public const string UsdtTokenAddress = "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9";

    /// <summary>Regtest backend wallet (first account of the fixed anvil seed).</summary>
    public const string BackendWalletAddress = "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266";

    /// <summary>TBTC/WETH Uniswap pool — the TBTC source on the fork.</summary>
    public const string TbtcFundingSourceAddress = "0xCb198a55e2a88841E855bE4EAcaad99422416b33";

    /// <summary>USDT0 OFT adapters on Arbitrum (native and legacy mesh) — the
    /// LayerZero bridge send emits <c>OFTSent</c> from one of these.</summary>
    public const string Usdt0NativeOftAddress = "0x14E4A1B13bf7F943c8ff7C51fb60FA964A298D92";

    public const string Usdt0LegacyOftAddress = "0x77652D5aba086137b595875263FC200182919B92";

    /// <summary>Circle CCTP v2 MessageTransmitter on Arbitrum — a burn emits
    /// <c>MessageSent</c> from it.</summary>
    public const string CctpMessageTransmitterAddress = "0x81D40F21F12A8F0E3252Bccb954D722d4c464B64";

    /// <summary>keccak256("OFTSent(bytes32,uint32,address,uint256,uint256)")</summary>
    public const string OftSentTopic = "0x85496b760a4b7f8d66384b9df21b381f5d1b1e79f229a47aaf4c232edc2fe59a";

    /// <summary>keccak256("MessageSent(bytes)")</summary>
    public const string MessageSentTopic = "0x8c5261668696ce22758910d05bab8f186d6eb247ceac2af2e82c7dc17669b036";

    /// <summary>0.1 TBTC (18 decimals) of backend lockup liquidity.</summary>
    public static readonly BigInteger BackendTbtcLiquidity = BigInteger.Parse("100000000000000000");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public static async Task<JsonNode?> RpcAsync(string method, params object[] parameters)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = new JsonArray(parameters
                .Select(p => p as JsonNode ?? JsonValue.Create(p))
                .ToArray<JsonNode?>()),
        };
        using var response = await Http.PostAsync(
            RpcUrl,
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (body?["error"] is { } error)
            throw new InvalidOperationException($"anvil-arb {method} failed: {error.ToJsonString()}");
        return body?["result"];
    }

    /// <summary>
    /// Fails fast (rather than silently passing) when anvil-arb is not a fork
    /// of Arbitrum One — without a real ARBITRUM_E2E_RPC_URL the stack starts a
    /// bare chain-42161 anvil that lacks every mainnet contract this flow needs.
    /// </summary>
    public static async Task AssertForkReadyAsync()
    {
        string chainId;
        try
        {
            chainId = (string?)await RpcAsync("eth_chainId") ?? "";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"anvil-arb is unreachable at {RpcUrl}. Start the regtest stack with `make regtest`.", ex);
        }

        if (ParseHex(chainId) != ArbitrumChainId)
            throw new InvalidOperationException($"anvil-arb reports chain id {chainId}, expected {ArbitrumChainId}.");

        var code = (string?)await RpcAsync("eth_getCode", TbtcTokenAddress, "latest");
        if (string.IsNullOrEmpty(code) || code == "0x")
            throw new InvalidOperationException(
                "anvil-arb is missing the TBTC token contract. It is not forking Arbitrum One — set ARBITRUM_E2E_RPC_URL in .env.local (see the repo README) and restart the stack.");
    }

    public static async Task<long> GetBlockNumberAsync() =>
        ParseHex((string?)await RpcAsync("eth_blockNumber") ?? "0x0");

    /// <summary>Logs with <paramref name="topic0"/> emitted by any of
    /// <paramref name="addresses"/> from <paramref name="fromBlock"/> to latest.</summary>
    public static async Task<JsonArray> GetLogsAsync(long fromBlock, string[] addresses, string topic0)
    {
        var filter = new JsonObject
        {
            ["fromBlock"] = $"0x{fromBlock:x}",
            ["toBlock"] = "latest",
            ["address"] = new JsonArray(addresses.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["topics"] = new JsonArray(topic0),
        };
        return await RpcAsync("eth_getLogs", filter) as JsonArray ?? [];
    }

    public static async Task<BigInteger> GetErc20BalanceAsync(string token, string owner)
    {
        var data = "0x70a08231" + PadAddress(owner);
        var result = (string?)await RpcAsync(
            "eth_call",
            new JsonObject { ["to"] = token, ["data"] = data },
            "latest");
        return ParseHexBig(result ?? "0x0");
    }

    /// <summary>
    /// Ensure the backend's Arbitrum wallet holds enough TBTC to lock up reverse
    /// swaps, transferring from the pool whale under anvil impersonation.
    /// </summary>
    public static async Task EnsureBackendTbtcLiquidityAsync()
    {
        if (await GetErc20BalanceAsync(TbtcTokenAddress, BackendWalletAddress) >= BackendTbtcLiquidity)
            return;

        await RpcAsync("anvil_setBalance", TbtcFundingSourceAddress, "0xde0b6b3a7640000"); // 1 ETH gas
        await RpcAsync("anvil_impersonateAccount", TbtcFundingSourceAddress);
        try
        {
            var data = "0xa9059cbb" + PadAddress(BackendWalletAddress) + PadUint(BackendTbtcLiquidity);
            var txHash = (string?)await RpcAsync(
                "eth_sendTransaction",
                new JsonObject
                {
                    ["from"] = TbtcFundingSourceAddress,
                    ["to"] = TbtcTokenAddress,
                    ["data"] = data,
                }) ?? throw new InvalidOperationException("TBTC funding transfer returned no tx hash.");
            await WaitForReceiptAsync(txHash);
        }
        finally
        {
            await RpcAsync("anvil_stopImpersonatingAccount", TbtcFundingSourceAddress);
        }
    }

    private static async Task WaitForReceiptAsync(string txHash)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var receipt = await RpcAsync("eth_getTransactionReceipt", txHash);
            if (receipt is not null)
            {
                var status = (string?)receipt["status"];
                if (status is not null && ParseHex(status) != 1)
                    throw new InvalidOperationException($"anvil-arb transaction {txHash} reverted.");
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"anvil-arb transaction {txHash} was not mined within 30s.");
    }

    private static string PadAddress(string address) =>
        address[2..].ToLowerInvariant().PadLeft(64, '0');

    private static string PadUint(BigInteger value) =>
        value.ToString("x", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(64, '0');

    private static long ParseHex(string hex) =>
        long.Parse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static BigInteger ParseHexBig(string hex) =>
        BigInteger.Parse("0" + hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
