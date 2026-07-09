using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Tests.End2End.Common;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Boltz swap coverage. ARK→LN submarine swaps go through the /send wizard
/// path (not the invoice payment-prompt path that hangs for
/// LN-receive), so they're tractable here. Reverse swaps (LN→ARK) are
/// intentionally out of scope — they hang in BTCPay's invoice pipeline,
/// a separate defect tracked elsewhere.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class SwapsTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public SwapsTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Fund an Arkade wallet, then pay an LND BOLT11 invoice from it via
    /// the /send wizard. The plugin routes a Lightning destination through a
    /// Boltz submarine swap; assert a Submarine ArkSwap is recorded for
    /// the wallet.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PayLightningInvoice_CreatesSubmarineSwap()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await FundStoreWalletViaNoteAsync(_fixture.ServerTester!, storeId, 200_000);

        // BOLT11 on the nigiri "lnd" node Boltz can route to.
        var bolt11 = await DockerHelper.CreateLndInvoice(amtSats: 20_000, expirySecs: 600);
        Assert.False(string.IsNullOrWhiteSpace(bolt11));

        // Wait on swap-eligible (LightningInvoice = non-recoverable) coins
        // being actually spendable, not the rendered balance. In the full
        // shared suite, note redemption can take longer than the generic
        // spendability timeout while earlier batch work drains.
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 20_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        // Manual coin selection with the polled non-recoverable outpoints:
        // Lightning sends reject recoverable (swept) coins, so pin the exact
        // selection rather than letting auto mode pick.
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/send").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = "CoinSelectionMode=manual" +
                       string.Concat(outpoints.Select(o => $"&selectedVtxoOutpoints={Uri.EscapeDataString(o)}")) +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(bolt11)}"
            });

        Assert.True(resp.Ok, $"send (LN) returned {resp.Status}");

        // The submarine swap is created synchronously during the spend;
        // poll the in-process swap storage briefly for it.
        var swapStorage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<ISwapStorage>();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var swaps = await swapStorage.GetSwaps(
                walletIds: [walletId!],
                swapTypes: [ArkSwapType.Submarine]);
            if (swaps.Count > 0)
            {
                Assert.Contains(swaps, s => s.SwapType == ArkSwapType.Submarine);
                return;
            }
            await Task.Delay(2_000);
        }
        Assert.Fail("no Submarine swap was recorded for the wallet after the LN spend");
    }

}
