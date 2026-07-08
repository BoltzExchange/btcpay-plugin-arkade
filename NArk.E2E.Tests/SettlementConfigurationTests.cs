using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

public class ArkadePaymentMethodConfigTests
{
    [Fact]
    public void SetSettlementOptionData_StoresGenericData()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject
                {
                    ["thresholdSats"] = "20000",
                    ["empty"] = ""
                });

        var option = Assert.Single(config.SettlementOptions!);
        Assert.Equal(StoreSettlementOptionKeys.BitcoinMainchain, option.Type);
        Assert.Equal("20000", option.GetAdditionalData("thresholdSats"));
        Assert.Null(option.GetAdditionalData("empty"));
    }

    [Fact]
    public void SetSettlementOptionData_SerializesStableTypeKey()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "20000" });
        var serializer = BlobSerializer.CreateSerializer().Serializer;

        var json = JObject.FromObject(config, serializer);
        var type = json.SelectToken("settlementOptions[0].type");

        Assert.NotNull(type);
        Assert.Equal(JTokenType.String, type!.Type);
        Assert.Equal(StoreSettlementOptionKeys.BitcoinMainchain, type.Value<string>());
    }

    [Fact]
    public void SetSettlementOptionData_RemovesOptionWhenDataIsNull()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "20000" })
            .SetSettlementOptionData(StoreSettlementOption.BitcoinMainchain, null);

        Assert.Null(config.SettlementOptions);
    }

    [Fact]
    public void SetSettlementOptionData_RemovesOptionWhenDataIsEmpty()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "20000" })
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "" });

        Assert.Null(config.SettlementOptions);
    }
}

[Collection("Arkade Plugin Tests")]
public class SettlementConfigurationUiTests : PlaywrightBaseTest
{
    private const string MainchainThresholdInput =
        "input[name='SettlementInputs[BitcoinMainchain].Data[thresholdSats]']";

    private readonly SharedPluginTestFixture _fixture;

    public SettlementConfigurationUiTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InitialSetup_PersistsMainchainSettlementThreshold()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStore();
        await ConfigureBtcOnchainWalletAsync(_fixture.ServerTester!, storeId);
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page!.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await Page.ClickAsync("[data-testid='hd-wallet-option']");
        await Page.WaitForSelectorAsync("#createNew [data-settlement-step]:not(.d-none)");
        Assert.Equal(0, await Page.Locator("#createNew [data-testid='create-wallet-next-btn']").CountAsync());
        Assert.True(await Page.Locator("#createNew [data-testid='settlement-threshold-input']").IsVisibleAsync());

        await Page.FillAsync($"#createNew {MainchainThresholdInput}", "25000");
        await Page.ClickAsync("#createNew [data-testid='create-wallet-btn']");

        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");

        Assert.Equal("25000", await Page.InputValueAsync(MainchainThresholdInput));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InitialSetup_DisablesMainchainSettlementWithoutBitcoinWallet()
    {
        _fixture.Initialize(this);
        await InitializePlaywright(_fixture.ServerTester!);

        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);

        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page!.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        Assert.Equal(0, await Page.Locator("[data-testid='settlement-threshold-input']").CountAsync());

        await Page.ClickAsync("[data-testid='hd-wallet-option']");
        await Page.WaitForSelectorAsync("#createNew [data-settlement-step]:not(.d-none)");
        Assert.Equal(0, await Page.Locator("#createNew [data-testid='create-wallet-next-btn']").CountAsync());

        var mainchainOption = Page.Locator("#createNew [data-testid='settlement-option-mainchain']");
        await Assertions.Expect(mainchainOption).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("#createNew [data-testid='settlement-option-mainchain-disabled']")).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("#createNew [data-testid='settlement-mainchain-requirement']")).ToContainTextAsync("Bitcoin on-chain wallet");
        Assert.Equal(0, await Page.Locator("#createNew [data-testid='settlement-threshold-input']").CountAsync());

        await Page.ClickAsync("#createNew [data-testid='create-wallet-btn']");

        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");

        await Assertions.Expect(Page.Locator("[data-testid='mainchain-settlement-unavailable']")).ToContainTextAsync("Bitcoin on-chain wallet");
        Assert.Equal(0, await Page.Locator(MainchainThresholdInput).CountAsync());
    }
}
