using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using NArk.Swaps.Models;
using Xunit;

namespace NArk.E2E.Tests;

public class StablecoinSwapClientTests
{
    [Theory]
    [InlineData("http://api", "http://rpc", "http://sponsor", true)]
    [InlineData(null, "http://rpc", "http://sponsor", false)]
    [InlineData("http://api", null, "http://sponsor", false)]
    [InlineData("http://api", "http://rpc", null, false)]
    [InlineData("http://api", "http://rpc", " ", false)]
    [InlineData(null, null, null, false)]
    public void EndpointOverrides_RequireAllWriteEndpoints(
        string? apiUrl, string? rpcUrl, string? sponsorUrl, bool expectedActive)
    {
        var overrides = new StablecoinEndpointOverrides(apiUrl, rpcUrl, sponsorUrl, null, false);
        Assert.Equal(expectedActive, overrides.Active);
    }

    [Fact]
    public void EndpointOverrides_InactiveLeaveSolanaRpcAtDefault()
    {
        var overrides = new StablecoinEndpointOverrides(null, null, null, "http://solana", false);
        Assert.Null(overrides.EffectiveSolanaRpcUrl);
    }

    [Fact]
    public void EndpointOverrides_ActiveNeverFallBackToProductionSolanaRpc()
    {
        var unset = new StablecoinEndpointOverrides("http://api", "http://rpc", "http://sponsor", null, false);
        Assert.Equal("http://127.0.0.1:9", unset.EffectiveSolanaRpcUrl);

        var set = new StablecoinEndpointOverrides("http://api", "http://rpc", "http://sponsor", "http://solana", false);
        Assert.Equal("http://solana", set.EffectiveSolanaRpcUrl);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void DeliveryPolling_CanOnlyBeDisabledForNonMainnetEndpointOverrides(
        bool mainnet,
        bool disableRequested,
        bool expected)
    {
        var overrides = new StablecoinEndpointOverrides(
            "http://api",
            "http://rpc",
            "http://sponsor",
            null,
            disableRequested);

        Assert.Equal(expected, overrides.ShouldDisableDeliveryPolling(mainnet));
    }

    [Theory]
    [InlineData(ArkSwapType.Submarine, 0, true)]
    [InlineData(ArkSwapType.ChainArkToBtc, 0, true)]
    [InlineData(ArkSwapType.ReverseSubmarine, 0, false)]
    [InlineData(ArkSwapType.ChainBtcToArk, 0, false)]
    [InlineData(ArkSwapType.Submarine, -2, false)]
    public void SettlementOnlyWaitsForRecentlyCreatedOutgoingSwaps(
        ArkSwapType swapType,
        int createdMinutesFromNow,
        bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var swap = new ArkSwap(
            "swap",
            "wallet",
            swapType,
            "invoice",
            1_000,
            "script",
            "address",
            ArkSwapStatus.Pending,
            null,
            now.AddMinutes(createdMinutesFromNow),
            now.AddMinutes(createdMinutesFromNow),
            "hash");

        Assert.Equal(
            expected,
            SettlementSchedulerService.IsRecentlyCreatedOutgoingSwap(
                swap,
                now - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void StoreMnemonic_DerivesStandardBip39Seed()
    {
        const string mnemonic =
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        const string expected =
            "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19" +
            "a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4";

        var seed = StablecoinSwapClient.DeriveWalletSeed(mnemonic);

        Assert.Equal(expected, Convert.ToHexStringLower(seed));
    }

    [Fact]
    public void StoreMnemonic_DerivationIsDeterministicAndWalletSpecific()
    {
        const string first =
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        const string second =
            "legal winner thank year wave sausage worth useful legal winner thank yellow";

        Assert.Equal(
            StablecoinSwapClient.DeriveWalletSeed(first),
            StablecoinSwapClient.DeriveWalletSeed(first));
        Assert.NotEqual(
            StablecoinSwapClient.DeriveWalletSeed(first),
            StablecoinSwapClient.DeriveWalletSeed(second));
    }
}
