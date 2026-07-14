using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Extensions.DependencyInjection;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Tests.End2End.Common;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Boltz swap coverage: ARK→LN submarine swaps through the /send wizard, ARK→BTC chain swaps,
/// and LN→ARK reverse swaps through BTCPay's invoice pipeline (a BTC-LN invoice on an
/// arkade-lightning store drives a reverse swap that settles the invoice once the hold
/// invoice is paid).
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
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);

        // In the full shared suite, note redemption can take longer than the generic
        // spendability timeout while earlier batch work drains.
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", 20_000, TimeSpan.FromMinutes(10));
        Assert.NotEmpty(outpoints);

        // Create the short-lived invoice only after the wallet is ready.
        var (bolt11, rHash) = await DockerHelper.CreateLndInvoiceWithHash(amtSats: 20_000, expirySecs: 600);
        Assert.False(string.IsNullOrWhiteSpace(bolt11));

        var swapStorage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<ISwapStorage>();
        var sendDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTimeOffset.UtcNow < sendDeadline)
        {
            // The batch scheduler can reserve the selected VTXOs between the spendability
            // poll and this POST. A validation failure renders the send view with HTTP 200,
            // so only a recorded swap proves success.
            var resp = await PostPluginFormAsync(storeId, "send",
                "CoinSelectionMode=manual" +
                string.Concat(outpoints.Select(o => $"&selectedVtxoOutpoints={Uri.EscapeDataString(o)}")) +
                $"&Outputs[0].Destination={Uri.EscapeDataString(bolt11)}");
            Assert.True(resp.Ok, $"send (LN) returned {resp.Status}");

            var swaps = await swapStorage.GetSwaps(
                walletIds: [walletId!],
                swapTypes: [ArkSwapType.Submarine]);
            if (swaps.Count > 0)
            {
                // Completion: the swap settles AND the receiving LND node was actually paid.
                await WaitForSwapAsync(
                    _fixture.ServerTester!.PayTester.ServiceProvider,
                    walletId!,
                    ArkSwapType.Submarine,
                    [ArkSwapStatus.Settled],
                    TimeSpan.FromMinutes(3));

                var (state, amtPaidSats) = await DockerHelper.LndLookupInvoice(rHash);
                Assert.Equal("SETTLED", state);
                Assert.Equal(20_000, amtPaidSats);
                return;
            }

            await Task.Delay(500);
            var remaining = sendDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            outpoints = await PollForSpendableCoinsAsync(
                storeId, "LightningInvoice", 20_000, remaining);
        }
        Assert.Fail("no Submarine swap was recorded after retrying the LN spend");
    }

    /// <summary>
    /// LN receive end-to-end: enable arkade Lightning, create a BTC-LN invoice (BTCPay's
    /// payment-method activation initiates the LN→ARK reverse swap and exposes Boltz's hold
    /// invoice as the destination), pay it from the regtest LND node, and assert both halves
    /// settle — the ReverseSubmarine swap record and the BTCPay invoice itself.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReceiveLightning_ReverseSwapSettlesInvoice()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var enableResp = await PostPluginFormAsync(storeId, "enable-ln");
        Assert.True(enableResp.Ok, $"enable-ln returned {enableResp.Status}");

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);

        // Invoice creation activates the BTC-LN method, which creates the reverse swap and
        // returns Boltz's hold invoice as the destination. Activation errors are swallowed
        // per-method by BTCPay — the destination just stays missing — so retry with a fresh
        // invoice a couple of times before failing.
        string? bolt11 = null;
        var invoiceId = "";
        for (var attempt = 0; attempt < 3 && bolt11 is null; attempt++)
        {
            var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
            {
                Amount = 20_000,
                Currency = "SATS",
                Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["BTC-LN"] }
            });
            invoiceId = invoice.Id;

            var activationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < activationDeadline)
            {
                var methods = await client.GetInvoicePaymentMethods(storeId, invoice.Id);
                var ln = methods.FirstOrDefault(m => m.PaymentMethodId == "BTC-LN");
                if (!string.IsNullOrEmpty(ln?.Destination))
                {
                    bolt11 = ln!.Destination;
                    break;
                }
                await Task.Delay(500);
            }
        }
        Assert.False(string.IsNullOrEmpty(bolt11),
            "BTC-LN payment method never activated with a hold invoice (reverse swap creation failed)");

        // The reverse swap exists before anything is paid.
        var swapStorage = _fixture.ServerTester!.PayTester.ServiceProvider
            .GetRequiredService<ISwapStorage>();
        var created = await swapStorage.GetSwaps(
            walletIds: [walletId!],
            swapTypes: [ArkSwapType.ReverseSubmarine],
            invoices: [bolt11!]);
        Assert.NotEmpty(created);

        // Paying a hold invoice blocks until Boltz claims, i.e. until the swap funds.
        using var payCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await DockerHelper.PayLndInvoice(bolt11!, payCts.Token);

        // The swap record must reach Settled…
        await PollUntilAsync(async () =>
            (await swapStorage.GetSwaps(
                walletIds: [walletId!],
                swapTypes: [ArkSwapType.ReverseSubmarine],
                invoices: [bolt11!],
                status: [ArkSwapStatus.Settled])).Count > 0,
            TimeSpan.FromMinutes(3),
            "reverse swap never reached Settled after the hold invoice was paid");

        // …and the BTCPay invoice must settle through the arkade Lightning listener.
        await WaitForInvoiceSettledAsync(client, storeId, invoiceId, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Fund an Arkade wallet, then settle to a bitcoin address via the /send wizard in
    /// Arkade (not Batch) mode. The plugin routes an on-chain destination through a Boltz
    /// ARK→BTC chain swap — the same mechanism as the Greenfield /arkade/send path — rather
    /// than joining it into a batch; assert a chain swap is recorded for the wallet.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SendBitcoin_ArkadeMode_CreatesChainSwap()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);

        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "BitcoinAddress", 20_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(outpoints);

        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();
        var destination = $"bitcoin:{bitcoinAddress}?amount=0.0002";

        // Chain swaps run their own coin selection; the polled outpoints are passed only to
        // satisfy the wizard's validation (auto mode re-selects if they drift under batching).
        var resp = await PostPluginFormAsync(storeId, "send",
            "CoinSelectionMode=auto&SpendType=Arkade" +
            string.Concat(outpoints.Select(o => $"&selectedVtxoOutpoints={Uri.EscapeDataString(o)}")) +
            $"&Outputs[0].Destination={Uri.EscapeDataString(destination)}" +
            $"&Outputs[0].AmountBtc={(20_000 / 100_000_000m).ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        Assert.True(resp.Ok, $"send (bitcoin) returned {resp.Status}: {await resp.TextAsync()}");

        // Arkade mode must settle via a chain swap, not a batch intent — and the swap must
        // actually deliver: Settled status plus BTC arriving at the destination address.
        await WaitForSwapAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider,
            walletId!,
            ArkSwapType.ChainArkToBtc,
            [ArkSwapStatus.Settled],
            TimeSpan.FromMinutes(3),
            mineWhileWaiting: true);

        var receivedSats = await DockerHelper.BitcoinGetReceivedByAddressSats(bitcoinAddress, minConf: 0);
        Assert.True(receivedSats > 0,
            $"chain swap settled but no BTC arrived at {bitcoinAddress}");
    }

}
