using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Verifies the plugin's user-facing pages return 200 OK for a logged-in
/// store admin, that initial-setup redirects once a wallet exists, and that
/// unauthenticated requests are challenged. One store serves every check —
/// the per-page render cost is a GET, not a fresh wallet. Uses
/// Page.Context.APIRequest.GetAsync rather than Playwright navigation so the
/// long-polling overview page doesn't block subsequent requests on
/// Chromium's per-origin connection pool.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class PageRenderTests : PlaywrightBaseTest
{
    private static readonly string[] Subpaths =
        ["getting-started", "overview", "receive", "send", "settings", "contracts", "swaps"];

    private readonly SharedPluginTestFixture _fixture;

    public PageRenderTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PluginPages_RenderRedirectAndEnforceAuth()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        foreach (var subpath in Subpaths)
        {
            var resp = await Page!.Context.APIRequest.GetAsync(
                new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/{subpath}").AbsoluteUri);

            Assert.True(resp.Ok, $"/plugins/ark/stores/.../{subpath} returned {resp.Status}");
            var html = await resp.TextAsync();
            // Sanity check: pages should at minimum have the BTCPay layout
            // (header with the store dropdown). If they returned a 200 but
            // rendered an error page, this catches it.
            Assert.Contains("BTCPay", html);
        }

        // The initial-setup endpoint should redirect (302) to /overview once
        // the wallet is configured — the wizard is for first-time setup only.
        // MaxRedirects = 0 surfaces the raw 302; the default follows it and
        // the assertion would run against the wrong URL.
        var setupResp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/initial-setup").AbsoluteUri,
            new APIRequestContextOptions { MaxRedirects = 0 });
        Assert.Equal(302, setupResp.Status);
        var location = setupResp.Headers["location"];
        Assert.NotNull(location);
        Assert.Contains("/overview", location);

        // Plugin endpoints under /plugins/ark/* require store-modify
        // permission (cookie auth). A brand new browser context has no
        // session cookie; BTCPay returns either 302 (redirect to login) or
        // 401/403 depending on the route's auth scheme — only 200 would be
        // a bug.
        var anonymousContext = await Browser!.NewContextAsync();
        var anonResp = await anonymousContext.APIRequest.GetAsync(
            new Uri(ServerUri!, "/plugins/ark/stores/some-fake-id/overview").AbsoluteUri,
            new APIRequestContextOptions { MaxRedirects = 0 });
        Assert.True(
            anonResp.Status is 302 or 401 or 403,
            $"Expected auth challenge (302/401/403), got {anonResp.Status}");
    }
}
