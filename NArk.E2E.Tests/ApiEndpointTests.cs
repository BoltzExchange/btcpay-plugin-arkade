using System.Text.Json;
using Microsoft.Playwright;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Covers the plugin's JSON API endpoints (parse-destination,
/// show-private-key). These don't involve BTCPay's invoice-creation
/// pipeline. All parse-destination classifications share one store — the
/// endpoint is stateless per request, so a fresh wallet per input buys
/// nothing.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class ApiEndpointTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public ApiEndpointTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// POST /parse-destination across the supported input shapes:
    /// an Arkade address resolves as ArkAddress, a BOLT11 invoice as
    /// LightningInvoice, while a bare BTC address (chain-swap destinations
    /// go through a separate flow) and garbage are rejected with
    /// IsValid=false rather than a throw — the wizard surfaces the Error
    /// string to the user.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ParseDestination_ClassifiesSupportedAndRejectsInvalidInputs()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var arkAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, storeId);

        var arkJson = await ParseDestinationAsync(storeId, arkAddr);
        // ParseDestinationResponse.Type is the SendDestinationType enum
        // stringified. For a bare Ark address that's "ArkAddress".
        Assert.Equal("ArkAddress", arkJson.GetProperty("type").GetString());

        var bolt11 = await NArk.Tests.End2End.Common.DockerHelper.CreateLndInvoice(
            amtSats: 1_000, expirySecs: 300);
        var lnJson = await ParseDestinationAsync(storeId, bolt11);
        Assert.Equal("LightningInvoice", lnJson.GetProperty("type").GetString());
        Assert.True(lnJson.GetProperty("isLightning").GetBoolean(),
            "isLightning should be true for a bolt11 invoice");

        var btcAddr = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
        var btcJson = await ParseDestinationAsync(storeId, btcAddr);
        Assert.False(
            btcJson.TryGetProperty("isValid", out var btcValid) && btcValid.GetBoolean(),
            "bare BTC address should not be a valid /send destination");

        var garbageJson = await ParseDestinationAsync(storeId, "obviously-not-a-real-address");
        Assert.False(
            garbageJson.TryGetProperty("isValid", out var garbageValid) && garbageValid.GetBoolean(),
            "garbage destination should not be marked valid");
    }

    private async Task<JsonElement> ParseDestinationAsync(string storeId, string destination)
    {
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/parse-destination").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = (await GetAntiforgeryTokenAsync()) ?? ""
                },
                DataObject = new { destination, amountBtc = (decimal?)null }
            });

        Assert.True(resp.Ok, $"parse-destination({destination}) returned {resp.Status}: {await resp.TextAsync()}");
        var json = await resp.JsonAsync();
        Assert.NotNull(json);
        return json!.Value;
    }

    /// <summary>
    /// POST /show-private-key on an admin-owned store should redirect to
    /// BTCPay's /recovery-seed-backup page rendering the wallet's secret
    /// in a <c>data-mnemonic</c> attribute. We don't follow the redirect
    /// via APIRequest (the controller uses TempData which only persists
    /// across cookie-bound page navigations); instead we POST the form
    /// from the settings page and let Playwright handle the redirect.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ShowPrivateKey_RevealsWalletSecret()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var expectedMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var storeId = await CreateStoreWithArkWalletAsync(expectedMnemonic);
        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");

        // The Show Private Key form submits via JS — we submit the same
        // form by element click. After submission the page lands on
        // /recovery-seed-backup with the mnemonic in #RecoveryPhrase.
        await Page!.EvaluateAsync(
            "document.querySelector('[data-testid=\"show-private-key-btn\"]').closest('form').submit()");
        await Page.WaitForURLAsync(
            url => url.Contains("/recovery-seed-backup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        var revealedMnemonic = await Page.GetAttributeAsync("#RecoveryPhrase", "data-mnemonic");
        Assert.Equal(expectedMnemonic, revealedMnemonic);
    }
}
