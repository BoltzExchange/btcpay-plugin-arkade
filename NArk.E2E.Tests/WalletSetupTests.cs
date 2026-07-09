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

    /// <summary>The new-wallet path creates a wallet and lands on overview.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateNewHotWallet_LandsOnOverview()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        Assert.Contains($"/plugins/ark/stores/{storeId}/overview", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
        Assert.DoesNotContain("/recovery-seed-backup", Page.Url);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreateNewHotWallet_DefersSeedBackupReminderUntilFundsArrive()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        Assert.Contains($"/plugins/ark/stores/{storeId}/overview", Page!.Url);
        Assert.DoesNotContain("/recovery-seed-backup", Page.Url);
        Assert.Equal(0, await Page.Locator("[data-testid='wallet-backup-warning']").CountAsync());

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

    /// <summary>Importing a BIP-39 seed phrase creates an HD wallet.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportBip39SeedPhrase_StoresHdWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var storeId = await CreateStoreWithArkWalletAsync(mnemonic);

        Assert.Contains($"/plugins/ark/stores/{storeId}", Page!.Url);
        Assert.DoesNotContain("/initial-setup", Page.Url);
    }

    /// <summary>Unsupported wallet input re-renders setup with an error.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvalidWalletInput_ShowsValidationError()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/initial-setup");

        await OpenImportWalletSettlementStepAsync("not-a-valid-wallet-format-xyzzy");
        await Page!.ClickAsync("#importExisting [data-testid='import-wallet-btn']");

        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains("/initial-setup", Page.Url);
        var bodyText = await Page.InnerTextAsync("body");
        var sawError = bodyText.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ||
                       bodyText.Contains("Could not update wallet", StringComparison.OrdinalIgnoreCase);
        Assert.True(sawError, $"Expected an error message but page body was:\n{bodyText[..Math.Min(500, bodyText.Length)]}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Overview_DeepActivityPagesOnlyShowInWalletDetails()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");

        Assert.Equal(0, await Page!.Locator("#StoreNav-Swaps, #StoreNav-Vtxos, #StoreNav-Intents").CountAsync());

        await Page.ClickAsync("[data-testid='wallet-details-toggle']");
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-vtxos-link']").CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-intents-link']").CountAsync());
        Assert.Equal(1, await Page.Locator("[data-testid='wallet-details-swaps-link']").CountAsync());
    }

    /// <summary>Arkade receive addresses are rejected as wallet imports.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportArkadeAddress_IsRejected()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var donorStoreId = await CreateStoreWithArkWalletAsync();
        var donorAddress = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, donorStoreId);

        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await OpenImportWalletSettlementStepAsync(donorAddress);
        await Page.ClickAsync("#importExisting [data-testid='import-wallet-btn']");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains("/initial-setup", Page.Url);
        var bodyText = await Page.InnerTextAsync("body");
        Assert.Contains("Unsupported", bodyText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Importing an existing wallet id reuses that wallet.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImportWalletId_ReusesExistingWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeAId = await CreateStoreWithArkWalletAsync();
        await GoToUrl($"/plugins/ark/stores/{storeAId}/overview");
        var walletId = await Page!.GetAttributeAsync(".truncate-center-id", "data-text");
        Assert.False(string.IsNullOrWhiteSpace(walletId), "store A has no wallet id");

        var storeBId = await CreateStoreWithArkWalletAsync(walletId);
        Assert.NotEqual(storeAId, storeBId);
        Assert.DoesNotContain("/initial-setup", Page.Url);

        // Verify store B's overview shows the same wallet id.
        await GoToUrl($"/plugins/ark/stores/{storeBId}/overview");
        var storeBWalletId = await Page.GetAttributeAsync(".truncate-center-id", "data-text");
        Assert.Equal(walletId, storeBWalletId);
    }

    /// <summary>
    /// Download the per-wallet diagnostic log file. The endpoint returns
    /// 200 + a file body even when no log lines have been written yet.
    /// (Regression target for PR #46 — added the wallet-log feature.)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WalletLogDownload_ReturnsFile()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        var resp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/wallet-log").AbsoluteUri);
        Assert.True(resp.Ok, $"wallet-log endpoint returned {resp.Status}");
    }

}
