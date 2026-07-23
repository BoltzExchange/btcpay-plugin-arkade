using Microsoft.Playwright;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

[Collection("Arkade Plugin Tests")]
public class WalletSetupTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public WalletSetupTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>Fresh stores land on the Arkade getting-started flow.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RegisterAndCreateStore_NavigateToArkWallet_ShowsSetupPage()
    {
        _fixture.Initialize(this);
        var server = _fixture.ServerTester!;

        await InitializePlaywrightAndRegisterAdminAsync(server);

        var storeId = await CreateStore();

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        await Page!.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains("/getting-started", Page.Url);
        Assert.Equal(1, await Page.Locator("[data-testid='getting-started-continue-btn']").CountAsync());
        Assert.Equal(0, await Page.Locator("[data-testid='hd-wallet-option']").CountAsync());

        await Page.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        var hdOption = Page.Locator("[data-testid='hd-wallet-option']");
        var importOption = Page.Locator("[data-testid='import-wallet-option']");

        Assert.Equal(1, await hdOption.CountAsync());
        Assert.Equal(1, await importOption.CountAsync());
    }

    /// <summary>
    /// The full hot-wallet journey on a single store: creation lands on
    /// overview (no setup/backup detour), deep activity pages only show
    /// inside the wallet-details panel, the wallet-log download endpoint
    /// returns a file (regression target for PR #46), and the seed-backup
    /// reminder stays hidden until funds arrive, then clears on mark-done.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateNewHotWallet_OverviewJourney_DefersSeedBackupUntilFundsArrive()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        Assert.Contains($"/plugins/ark/stores/{storeId}/overview", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
        Assert.DoesNotContain("/recovery-seed-backup", Page.Url);
        Assert.Equal(0, await Page.Locator("[data-testid='wallet-backup-warning']").CountAsync());

        // Deep activity pages (swaps/vtxos/intents) live behind the
        // wallet-details toggle, not in the store nav.
        Assert.Equal(0, await Page.Locator("#StoreNav-Swaps, #StoreNav-Vtxos, #StoreNav-Intents").CountAsync());
        await Page.ClickAsync("[data-testid='wallet-details-toggle']");
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-vtxos-link']").CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-intents-link']").CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-swaps-link']").CountAsync());

        // The per-wallet diagnostic log endpoint returns 200 + a file body
        // even before any log lines have been written.
        var logResp = await Page.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/wallet-log").AbsoluteUri,
            new APIRequestContextOptions { MaxRedirects = 0 });
        Assert.True(logResp.Ok, $"wallet-log endpoint returned {logResp.Status}");

        await FundStoreWalletViaNoteAsync(_fixture.ServerTester!, storeId, 50_000);
        await WaitForVisibleSelectorAsync(
            $"/plugins/ark/stores/{storeId}/overview",
            "[data-testid='wallet-backup-warning']",
            TimeSpan.FromMinutes(3));

        var backupWarning = Page.Locator("[data-testid='wallet-backup-warning']");
        Assert.Equal(1, await backupWarning.CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='backup-show-seed-btn']").CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='backup-mark-done-btn']").CountAsync());

        await Page.ClickAsync("[data-testid='backup-mark-done-btn']");
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        Assert.Equal(0, await Page.Locator("[data-testid='wallet-backup-warning']").CountAsync());

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");
        var settingsBody = await Page.InnerTextAsync("body");
        Assert.Contains("Recovery seed", settingsBody);
        Assert.Contains("Show seed", settingsBody);
        Assert.DoesNotContain("not backed up", settingsBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Import-path validation on a single donor/importer pair. The donor
    /// store imports a fresh BIP-39 mnemonic (covering the successful
    /// seed-import path); the importing store then tries every rejected
    /// input against the same setup page: unsupported garbage, an Arkade
    /// receive address, another store's wallet id, and a mnemonic already
    /// in use by another store.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportWallet_RejectsUnsupportedAndForeignInputs()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var donorStoreId = await CreateStoreWithArkWalletAsync(mnemonic);
        Assert.Contains($"/plugins/ark/stores/{donorStoreId}", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
        var donorAddress = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, donorStoreId);
        var donorWalletId = await GetStoreWalletIdAsync(donorStoreId);
        Assert.False(string.IsNullOrWhiteSpace(donorWalletId), "donor store has no wallet id");

        var storeId = await CreateStore();

        // Garbage may surface either as "Unsupported" or the generic
        // update failure, matching the pre-merge tolerance.
        await AssertImportRejectedAsync(storeId, "not-a-valid-wallet-format-xyzzy",
            "Unsupported", "Could not update wallet");
        await AssertImportRejectedAsync(storeId, donorAddress, "Unsupported");
        await AssertImportRejectedAsync(storeId, donorWalletId!, "Unsupported");
        await AssertImportRejectedAsync(storeId, mnemonic, "already in use by another store");
    }

    private async Task AssertImportRejectedAsync(
        string storeId, string walletInput, params string[] expectedErrors)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/initial-setup");

        await OpenImportWalletSettlementStepAsync(walletInput);
        await Page!.ClickAsync("#importExisting [data-testid='import-wallet-btn']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains("/initial-setup", Page.Url);
        var bodyText = await Page.InnerTextAsync("body");
        Assert.True(
            expectedErrors.Any(error => bodyText.Contains(error, StringComparison.OrdinalIgnoreCase)),
            $"Expected one of [{string.Join(", ", expectedErrors)}] for input '{walletInput}' " +
            $"but page body was:\n{bodyText[..Math.Min(500, bodyText.Length)]}");
    }
}
