using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Unhappy-path coverage for the offchain spend endpoints on an empty
/// wallet: /suggest-coins reports "no spendable coins" instead of throwing,
/// and a /send with no coins selected surfaces a validation error and moves
/// no funds. (The funded spend path is covered by FundedWalletTests and
/// OverviewActivityTests.)
/// </summary>
[Collection("Arkade Plugin Tests")]
public class SpendingTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public SpendingTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EmptyWallet_SuggestCoinsAndSend_SurfaceValidationErrors()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        // /suggest-coins on an empty wallet returns the "no spendable
        // coins" error rather than throwing.
        var suggestResp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/suggest-coins").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destinationType = "ArkAddress", amountSats = 10_000L }
            });

        Assert.True(suggestResp.Ok, $"suggest-coins returned {suggestResp.Status}");
        var json = await suggestResp.JsonAsync();
        var error = json!.Value.TryGetProperty("error", out var e) ? e.GetString() : null;
        Assert.False(string.IsNullOrEmpty(error), "empty wallet should report no spendable coins");

        // No selectedVtxoOutpoints and no auto CoinSelectionMode → "No coins selected".
        var sendResp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/send").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"Outputs%5B0%5D.Destination={Uri.EscapeDataString(recipientAddr)}" +
                       "&Outputs%5B0%5D.AmountBtc=0.0001"
            });

        Assert.True(sendResp.Ok, $"send returned {sendResp.Status}");
        var html = await sendResp.TextAsync();
        Assert.Contains("No coins selected", html);
    }
}
