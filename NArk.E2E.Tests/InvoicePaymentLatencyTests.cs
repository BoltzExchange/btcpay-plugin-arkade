using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Times the click-to-paid latency of an Arkade invoice when funded via
/// the checkout cheat-mode (out-of-round <c>ark send</c> from inside the
/// arkd container, ~instant on arkd's side) and asserts the invoice
/// transitions to <c>Settled</c> within a regression threshold.
///
/// Two things this guards against:
///
/// 1. <b>Latency regression.</b> The current observed click-to-paid time
///    is ~6s, dominated by an unexplained gap between <c>UpsertVtxo</c>
///    firing <c>VtxosChanged</c> and BTCPay's invoice settling. We want a
///    test that fails loudly if a future change pushes this past
///    <see cref="LatencyThreshold"/>.
///
/// 2. <b>Missed-event regression.</b> If a refactor causes the plugin to
///    not deliver <c>VtxosChanged</c> at all, the invoice will hang at
///    <c>New</c> indefinitely. The test's outer timeout
///    (<see cref="HardTimeout"/>) catches that as a hard failure rather
///    than a flake.
///
/// The first iteration in a fresh test environment initialises the
/// in-arkd <c>ark</c> CLI and funds it via a single boarding+settle
/// (one batch round, kept out of the timed window). Subsequent
/// iterations on the same suite reuse the funded CLI.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class InvoicePaymentLatencyTests : PlaywrightBaseTest
{
    /// <summary>
    /// Outer timeout — if the invoice never settles inside this window,
    /// fail the test as a missed-event regression (not a flake).
    /// </summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pass-fail threshold for the timed latency. As of writing,
    /// observed real-world is ~6s; this is set with ~2x headroom against
    /// CI jitter. Tighten as we localise and fix the in-handler gap.
    /// </summary>
    private static readonly TimeSpan LatencyThreshold = TimeSpan.FromSeconds(12);

    private readonly SharedPluginTestFixture _fixture;

    public InvoicePaymentLatencyTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheatModePay_DirectArkTx_InvoiceSettlesWithinThreshold()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithSingleKeyWalletAsync();

        // Funding the in-arkd ark CLI requires one batch round (~10s).
        // Deliberately do this BEFORE the timing window — we want to
        // measure the plugin's invoice-settle latency in isolation, not
        // the batch round.
        await EnsureArkdCliReadyAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            // SATS amount so the cheat extension routes a 5000-sat send
            // (matches what a manual cheat-pay button click would do).
            Amount = 5000m,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = new[] { "ARKADE" }
            }
        });
        Assert.False(string.IsNullOrEmpty(invoice.Id));

        // BTCPay enforces antiforgery on every UI controller POST (see
        // UIControllerAntiforgeryTokenAttribute registered in Startup);
        // missing token = 400 with empty body. Grab one off any page
        // that rendered a form — the Arkade overview always does.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        // Start the clock and trigger the cheat-mode pay. The
        // BTCPay route POST /i/{id}/test-payment invokes
        // ArkadeCheckoutCheatModeExtension.PayInvoice which shells out
        // `docker exec <arkd> ark send` — an out-of-round Ark tx that
        // arkd processes essentially instantly.
        var t0 = DateTimeOffset.UtcNow;
        // BTCPay's UIInvoiceController.TestPayment doesn't decorate its
        // request param with [FromBody], so ASP.NET MVC binds from form
        // data — not JSON. Sending JSON silently falls back to default
        // values (`PaymentMethodId="BTC"`) and the cheat-extension lookup
        // returns null. Use form-urlencoded to match the binder.
        var payResp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/i/{invoice.Id}/test-payment").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                    ["RequestVerificationToken"] = token
                },
                Data = "Amount=5000&CryptoCode=SATS&PaymentMethodId=ARKADE"
            });

        var payBody = await payResp.TextAsync();
        Assert.True(payResp.Ok,
            $"POST /i/{invoice.Id}/test-payment returned {payResp.Status}: {payBody}");

        // Poll the invoice status. We want to FAIL the test if the
        // invoice doesn't settle (missed event) AND record the elapsed
        // time when it does (latency regression). Tight loop —
        // resolution matters at the sub-second level.
        var deadline = t0 + HardTimeout;
        InvoiceStatus lastStatus = InvoiceStatus.New;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await client.GetInvoice(storeId, invoice.Id);
            lastStatus = current.Status;
            if (current.Status == InvoiceStatus.Settled)
            {
                var elapsed = DateTimeOffset.UtcNow - t0;
                TestLogs.LogInformation(
                    $"Invoice {invoice.Id} settled in {elapsed.TotalSeconds:F2}s " +
                    $"(threshold: {LatencyThreshold.TotalSeconds:F0}s)");
                Assert.True(elapsed <= LatencyThreshold,
                    $"Latency regression: invoice settled in {elapsed.TotalSeconds:F2}s, " +
                    $"threshold is {LatencyThreshold.TotalSeconds:F0}s. " +
                    "Pull the plugin debug log and look at the gap between " +
                    "`UpsertVtxo: inserted` and `invoice_paymentSettled` for the source.");
                return;
            }
            if (current.Status is InvoiceStatus.Invalid)
                Assert.Fail($"Invoice transitioned to Invalid (last: {lastStatus}) — payment was rejected, not just slow.");
            await Task.Delay(100);
        }

        // Timeout. This is the missed-event regression path — the cheat-mode
        // pay returned an OK txid (we asserted .Ok above) but the plugin's
        // VtxosChanged → OnVtxoChanged → AddPayment → InvoiceWatcher chain
        // never carried the payment through to Settled.
        Assert.Fail(
            $"Invoice {invoice.Id} never reached Settled within {HardTimeout.TotalSeconds:F0}s " +
            $"(last status: {lastStatus}). Likely a missed VtxosChanged event or a broken " +
            "subscriber on the path UpsertVtxo → OnVtxoChanged → paymentService.AddPayment.");
    }

}
