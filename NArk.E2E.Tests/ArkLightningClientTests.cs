using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using NArk.Swaps.Models;
using NBitcoin;
using Xunit;

namespace NArk.E2E.Tests;

public class ArkLightningClientTests
{
    // BOLT11 spec test vector: 2500 uBTC on mainnet.
    private const string Bolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    [Theory]
    [InlineData(ArkSwapStatus.Pending, LightningInvoiceStatus.Unpaid)]
    [InlineData(ArkSwapStatus.Settled, LightningInvoiceStatus.Paid)]
    [InlineData(ArkSwapStatus.Failed, LightningInvoiceStatus.Expired)]
    [InlineData(ArkSwapStatus.Refunded, LightningInvoiceStatus.Expired)]
    [InlineData(ArkSwapStatus.Unknown, LightningInvoiceStatus.Unpaid)]
    public void Map_MapsEverySwapStatus(ArkSwapStatus swapStatus, LightningInvoiceStatus expected)
    {
        var invoice = ArkLightningClient.Map(CreateSwap(swapStatus), null, Network.Main);

        Assert.Equal(expected, invoice.Status);
    }

    [Fact]
    public void Map_NeverThrowsForAnySwapStatus()
    {
        // Core's LightningListener calls GetInvoice unguarded: a Map throw for any
        // status would abort the store's whole Lightning listener.
        foreach (var status in Enum.GetValues<ArkSwapStatus>())
        {
            ArkLightningClient.Map(CreateSwap(status), null, Network.Main);
        }
    }

    private static ArkSwap CreateSwap(ArkSwapStatus status) => new(
        SwapId: "swap-id",
        WalletId: "wallet-id",
        SwapType: ArkSwapType.ReverseSubmarine,
        Invoice: Bolt11,
        ExpectedAmount: 250_000,
        ContractScript: "",
        Address: "",
        Status: status,
        FailReason: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Hash: "");
}
