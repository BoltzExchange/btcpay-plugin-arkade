using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Swaps.Boltz;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

[Collection("Arkade Plugin Tests")]
public class SettlementTriggerTests : PlaywrightBaseTest
{
    private static readonly PaymentMethodId ArkadePaymentMethodId = new("ARKADE");
    private const string MainchainThresholdInput =
        "input[name='SettlementInputs[BitcoinMainchain].Data[thresholdSats]']";

    private readonly SharedPluginTestFixture _fixture;

    public SettlementTriggerTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ArkadeWalletBalance_AboveThreshold_StartsMainchainSettlement()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStore();
        await ConfigureBtcOnchainWalletAsync(_fixture.ServerTester!, storeId);
        await ConfigureArkadeWalletAsync(storeId, thresholdSats: 50_000);
        var walletId = await GetStoreArkadeWalletIdAsync(storeId);

        await EnsureArkdCliReadyAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 30_000);
        await PayArkadeInvoiceAsync(client, storeId, 30_000);

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        await WaitForChainSwapAsync(services, walletId, TimeSpan.FromMinutes(2));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ArkadeWalletBalance_AboveBoltzMax_CapsMainchainSettlementAmount()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var limitsValidator = services.GetRequiredService<BoltzLimitsValidator>();
        var limits = await limitsValidator.GetChainLimitsAsync(isBtcToArk: false) ??
            throw new InvalidOperationException("Boltz ARK to BTC limits were unavailable.");

        var storeId = await CreateStore();
        await ConfigureBtcOnchainWalletAsync(_fixture.ServerTester!, storeId);
        await ConfigureArkadeWalletAsync(storeId, thresholdSats: limits.MaxAmount);
        var walletId = await GetStoreArkadeWalletIdAsync(storeId);

        await EnsureArkdCliReadyAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, limits.MaxAmount + 10_000);

        var swap = await WaitForChainSwapAsync(services, walletId, TimeSpan.FromMinutes(2));
        Assert.True(
            swap.ExpectedAmount <= limits.MaxAmount,
            $"settlement swap amount {swap.ExpectedAmount} exceeded Boltz max {limits.MaxAmount}");

    }

    private async Task ConfigureArkadeWalletAsync(string storeId, long thresholdSats)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page!.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await OpenCreateWalletSettlementStepAsync();
        await Page.FillAsync($"#createNew {MainchainThresholdInput}", thresholdSats.ToString());
        await Page.ClickAsync("#createNew [data-testid='create-wallet-btn']");

        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
    }

    private async Task<string> GetStoreArkadeWalletIdAsync(string storeId)
    {
        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var storeRepository = services.GetRequiredService<StoreRepository>();
        var store = await storeRepository.FindStore(storeId);
        Assert.NotNull(store);

        var config = store.GetPaymentMethodConfig(ArkadePaymentMethodId);
        Assert.NotNull(config);
        return ReadString(config, "walletId") ??
               throw new InvalidOperationException("Stored Arkade config did not contain walletId.");
    }

    private static string? ReadString(Newtonsoft.Json.Linq.JToken token, string camelCaseName) =>
        token.Value<string>(camelCaseName) ??
        token.Value<string>(char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..]);
}
