using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
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

    /// <summary>
    /// The single mainchain-settlement trigger journey: a balance crossing
    /// the configured threshold starts a settlement chain swap, the swap
    /// amount is capped at the Boltz maximum, and the capped settlement
    /// completes. (Threshold crossing by accumulation over several payments
    /// is the same balance comparison; the stablecoin threshold flows cover
    /// the two-payment shape.)
    /// </summary>
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

        // The capped settlement must also complete, not just be recorded.
        await WaitForSwapAsync(services, walletId,
            ArkSwapType.ChainArkToBtc,
            [ArkSwapStatus.Settled],
            TimeSpan.FromMinutes(4), mineWhileWaiting: true);
    }

    private async Task ConfigureArkadeWalletAsync(string storeId, long thresholdSats)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page!.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await OpenCreateWalletSettlementStepAsync();
        await Page.CheckAsync("#createNew input[name='activeSettlement'][value='bitcoin-mainchain']");
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

}
