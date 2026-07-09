using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Exercises the offchain spend path: a funded wallet selects coins via
/// /suggest-coins and submits a transfer through the /send wizard to another
/// store's Arkade address. Both wallets are funded/observed through the
/// in-process IContractService note path (see FundedWalletTests).
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

    /// <summary>
    /// Unhappy path: the /send wizard with no coins selected (manual coin
    /// mode, empty selection) must surface a validation error and not move
    /// funds.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Send_NoCoinsSelected_ShowsError()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        // No selectedVtxoOutpoints and no auto CoinSelectionMode → "No coins selected".
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/send").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = UrlEncodeForm(new()
                {
                    ["Outputs[0].Destination"] = recipientAddr,
                    ["Outputs[0].AmountBtc"] = "0.0001"
                })
            });

        Assert.True(resp.Ok, $"send returned {resp.Status}");
        var html = await resp.TextAsync();
        Assert.Contains("No coins selected", html);
    }

    /// <summary>
    /// /suggest-coins on an empty wallet returns the "no spendable coins"
    /// error rather than throwing.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SuggestCoins_EmptyWallet_ReturnsNoCoinsError()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";

        var resp = await Page!.Context.APIRequest.PostAsync(
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

        Assert.True(resp.Ok, $"suggest-coins returned {resp.Status}");
        var json = await resp.JsonAsync();
        var error = json!.Value.TryGetProperty("error", out var e) ? e.GetString() : null;
        Assert.False(string.IsNullOrEmpty(error), "empty wallet should report no spendable coins");
    }

    // --- helpers ---

    private static string UrlEncodeForm(Dictionary<string, string> fields) =>
        string.Join("&", fields.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}
