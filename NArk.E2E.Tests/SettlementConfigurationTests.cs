using Boltz.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

[Trait("Category", "Unit")]
public class ArkadePaymentMethodConfigTests
{
    [Fact]
    public void SetSettlementOptionData_StoresGenericDataAndPrunesNullOrBlankValues()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject
                {
                    ["thresholdSats"] = "20000",
                    ["empty"] = "",
                    ["cleared"] = JValue.CreateNull()
                });

        var option = Assert.Single(config.SettlementOptions!);
        Assert.Equal(StoreSettlementOptionKeys.BitcoinMainchain, option.Type);
        Assert.Equal("20000", option.GetAdditionalData("thresholdSats"));
        Assert.Null(option.GetAdditionalData("empty"));
        Assert.False(option.AdditionalData!.ContainsKey("cleared"));
    }

    [Fact]
    public void ResolveActiveSettlement_ExplicitSelectionWins()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { [MainchainSettlementData.ThresholdKey] = "20000" })
            .SetActiveSettlement(StoreSettlementOption.Usd);

        Assert.Equal(StoreSettlementOptionKeys.Usd, config.ActiveSettlement);
        Assert.Equal(StoreSettlementOption.Usd, config.ResolveActiveSettlement());
    }

    [Fact]
    public void SetActiveSettlement_Null_MarksExplicitlyOff()
    {
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { [MainchainSettlementData.ThresholdKey] = "20000" })
            .SetActiveSettlement(null);

        Assert.Equal(StoreSettlementOptionKeys.None, config.ActiveSettlement);
        Assert.Null(config.ResolveActiveSettlement());
    }

    [Fact]
    public void ResolveActiveSettlement_WithoutStoredSelection_IsOff()
    {
        // Only an explicitly stored, recognized key activates a method: stored
        // option data alone (or an unknown key) never turns settlement on.
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { [MainchainSettlementData.ThresholdKey] = "20000" });

        Assert.Null(config.ActiveSettlement);
        Assert.Null(config.ResolveActiveSettlement());
        Assert.Null((config with { ActiveSettlement = "unknown-key" }).ResolveActiveSettlement());
    }

    [Fact]
    public void DeactivateSettlement_OnlyClearsWhenMethodIsActive()
    {
        var active = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetActiveSettlement(StoreSettlementOption.BitcoinMainchain);

        // Disabling the dormant method leaves the active one untouched.
        Assert.Equal(
            StoreSettlementOption.BitcoinMainchain,
            active.DeactivateSettlement(StoreSettlementOption.Usd).ResolveActiveSettlement());

        // Disabling the active method turns settlement off.
        Assert.Null(active.DeactivateSettlement(StoreSettlementOption.BitcoinMainchain).ResolveActiveSettlement());
    }

    [Fact]
    public void SetActiveSettlement_PreservesDormantMethodConfig()
    {
        var config = UsdSettlementConfiguration.Set(
                new ArkadePaymentMethodConfig(WalletId: "wallet")
                    .SetSettlementOptionData(
                        StoreSettlementOption.BitcoinMainchain,
                        new JObject { [MainchainSettlementData.ThresholdKey] = "20000" }),
                new UsdSettlementConfig(
                    50_000,
                    UsdSettlementData.DefaultDestinationChain,
                    "0xabc",
                    UsdSettlementData.DefaultAsset))
            .SetActiveSettlement(StoreSettlementOption.BitcoinMainchain);

        // Switching to mainchain keeps the stablecoin destination stored so the
        // merchant can switch back without re-entering it.
        Assert.Equal("0xabc", UsdSettlementConfiguration.Get(config)!.DestinationAddress);
        Assert.Equal("20000", config.GetSettlementOption(StoreSettlementOption.BitcoinMainchain)!.GetAdditionalData(MainchainSettlementData.ThresholdKey));
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
    public void UsdSettlementConfiguration_RoundTripsStableTypeAndFields()
    {
        var config = UsdSettlementConfiguration.Set(
            new ArkadePaymentMethodConfig(WalletId: "wallet")
                .SetSettlementOptionData(
                    StoreSettlementOption.BitcoinMainchain,
                    new JObject { [MainchainSettlementData.ThresholdKey] = "20000" }),
            new UsdSettlementConfig(
                50_000,
                UsdSettlementData.DefaultDestinationChain,
                "0x1234567890abcdef1234567890abcdef12345678",
                UsdSettlementData.UsdcAsset));
        var serializer = BlobSerializer.CreateSerializer().Serializer;

        var json = JObject.FromObject(config, serializer);
        var roundTripped = json.ToObject<ArkadePaymentMethodConfig>(serializer)!;
        var storedUsd = Assert.Single(
            roundTripped.SettlementOptions!,
            option => option.Type == StoreSettlementOptionKeys.Usd);
        var parsed = UsdSettlementConfiguration.Get(roundTripped);

        Assert.Equal(StoreSettlementOptionKeys.Usd, storedUsd.Type);
        Assert.Equal("50000", storedUsd.GetAdditionalData(UsdSettlementData.ThresholdKey));
        Assert.Equal(UsdSettlementData.DefaultDestinationChain, storedUsd.GetAdditionalData(UsdSettlementData.DestinationChainKey));
        Assert.Equal("0x1234567890abcdef1234567890abcdef12345678", storedUsd.GetAdditionalData(UsdSettlementData.DestinationAddressKey));
        Assert.Equal(UsdSettlementData.UsdcAsset, storedUsd.GetAdditionalData(UsdSettlementData.AssetKey));
        Assert.Null(storedUsd.GetAdditionalData("slippageBps"));
        Assert.NotNull(parsed);
        Assert.Equal(50_000, parsed.ThresholdSats);
        Assert.Contains(
            roundTripped.SettlementOptions!,
            option => option.Type == StoreSettlementOptionKeys.BitcoinMainchain);
    }

    [Theory]
    [InlineData("usdt", UsdSettlementData.UsdtAsset)]
    [InlineData("usdc", UsdSettlementData.UsdcAsset)]
    public void UsdSettlementConfiguration_CanonicalizesSupportedAssets(
        string inputAsset,
        string expectedAsset)
    {
        var result = UsdSettlementConfiguration.Parse(new SettlementInput
        {
            Data = new JObject
            {
                [UsdSettlementData.ThresholdKey] = "50000",
                [UsdSettlementData.DestinationChainKey] = "  Arbitrum One  ",
                [UsdSettlementData.DestinationAddressKey] = "  0x1234  ",
                [UsdSettlementData.AssetKey] = inputAsset
            }
        });

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Equal(UsdSettlementData.DefaultDestinationChain, result.Config.DestinationChain);
        Assert.Equal("0x1234", result.Config.DestinationAddress);
        Assert.Equal(expectedAsset, result.Config.Asset);
    }

    [Fact]
    public void UsdSettlementConfiguration_IgnoresAndDropsLegacySlippage()
    {
        var result = UsdSettlementConfiguration.Parse(new SettlementInput
        {
            Data = new JObject
            {
                [UsdSettlementData.ThresholdKey] = "50000",
                [UsdSettlementData.DestinationChainKey] = UsdSettlementData.DefaultDestinationChain,
                [UsdSettlementData.DestinationAddressKey] = "0x1234",
                [UsdSettlementData.AssetKey] = UsdSettlementData.DefaultAsset,
                ["slippageBps"] = "500"
            }
        });

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Null(UsdSettlementConfiguration.ToData(result.Config).Value<string>("slippageBps"));
    }

    [Theory]
    [InlineData("Arbitrum One", BindingAsset.Usdt)]
    [InlineData("Polygon PoS", BindingAsset.Usdt0)]
    public void UsdtSettlement_ResolvesTheNativeRouteWithoutChangingThePublicAsset(
        string chain,
        BindingAsset expectedBindingAsset)
    {
        BindingDestination[] destinations =
        [
            new("Arbitrum One", BindingAsset.Usdt, BindingBridgeKind.Direct),
            new("Polygon PoS", BindingAsset.Usdt0, BindingBridgeKind.Oft)
        ];

        var bindingAsset = UsdSettlementConfiguration.ResolveBindingAsset(
            destinations,
            chain,
            UsdSettlementData.UsdtAsset);

        Assert.Equal(expectedBindingAsset, bindingAsset);
    }

    [Fact]
    public void UsdSettlementConfiguration_LeavesNetworkUnselectedUntilConfigured()
    {
        var data = UsdSettlementConfiguration.GetViewData(
            new ArkadePaymentMethodConfig(WalletId: "wallet"),
            input: null);

        Assert.Null(data.Value<string>(UsdSettlementData.DestinationChainKey));
        Assert.Equal(
            UsdSettlementData.DefaultAsset,
            data.Value<string>(UsdSettlementData.AssetKey));
        Assert.Null(data.Value<string>("slippageBps"));
    }

    [Theory]
    [InlineData("thresholdSats", "-1", "thresholdSats")]
    [InlineData("thresholdSats", "1.5", "thresholdSats")]
    [InlineData("destinationChain", null, "destinationChain")]
    [InlineData("destinationAddress", "   ", "destinationAddress")]
    [InlineData("asset", "DAI", "asset")]
    public void UsdSettlementConfiguration_RejectsInvalidEnabledConfig(
        string field,
        string? value,
        string expectedInvalidField)
    {
        var data = new JObject
        {
            [UsdSettlementData.ThresholdKey] = "50000",
            [UsdSettlementData.DestinationChainKey] = UsdSettlementData.DefaultDestinationChain,
            [UsdSettlementData.DestinationAddressKey] = "0x1234",
            [UsdSettlementData.AssetKey] = UsdSettlementData.DefaultAsset
        };
        data[field] = value is null ? JValue.CreateNull() : value;

        var result = UsdSettlementConfiguration.Parse(new SettlementInput { Data = data });

        Assert.False(result.IsValid);
        Assert.Equal(expectedInvalidField, result.InvalidField);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UsdSettlementConfiguration_BlankThresholdWithoutAddressSkipsOption(string? threshold)
    {
        var result = UsdSettlementConfiguration.Parse(new SettlementInput
        {
            Data = new JObject
            {
                [UsdSettlementData.ThresholdKey] = threshold is null ? JValue.CreateNull() : threshold,
                [UsdSettlementData.AssetKey] = UsdSettlementData.DefaultAsset
            }
        });

        Assert.True(result.IsValid);
        Assert.Null(result.Config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void UsdSettlementConfiguration_BlankOrZeroThresholdUsesSwapMinimum(string? threshold)
    {
        var result = UsdSettlementConfiguration.Parse(new SettlementInput
        {
            Data = new JObject
            {
                [UsdSettlementData.ThresholdKey] = threshold is null ? JValue.CreateNull() : threshold,
                [UsdSettlementData.DestinationChainKey] = UsdSettlementData.DefaultDestinationChain,
                [UsdSettlementData.DestinationAddressKey] = "0x1234",
                [UsdSettlementData.AssetKey] = UsdSettlementData.DefaultAsset
            }
        });

        Assert.True(result.IsValid);
        Assert.NotNull(result.Config);
        Assert.Equal(0, result.Config.ThresholdSats);
    }

    // The same removal path fires for a null payload, a payload whose only
    // key is blank, and a payload of exclusively null/blank values.
    [Fact]
    public void SetSettlementOptionData_RemovesOptionWhenDataIsEffectivelyEmpty()
    {
        ArkadePaymentMethodConfig WithOption() => new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "20000" });

        Assert.Null(WithOption()
            .SetSettlementOptionData(StoreSettlementOption.BitcoinMainchain, null)
            .SettlementOptions);

        Assert.Null(WithOption()
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject { ["thresholdSats"] = "" })
            .SettlementOptions);

        Assert.Null(new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject
                {
                    ["a"] = "",
                    ["b"] = "   ",
                    ["c"] = JValue.CreateNull()
                })
            .SettlementOptions);
    }

    [Fact]
    public void SetSettlementOptionData_RoundTripsNonStringPayload()
    {
        var nested = new JObject { ["key"] = 99 };
        var config = new ArkadePaymentMethodConfig(WalletId: "wallet")
            .SetSettlementOptionData(
                StoreSettlementOption.BitcoinMainchain,
                new JObject
                {
                    ["thresholdSats"] = "20000",
                    ["amount"] = 42,
                    ["enabled"] = true,
                    ["nested"] = nested
                });
        var serializer = BlobSerializer.CreateSerializer().Serializer;

        var json = JObject.FromObject(config, serializer);
        var roundTripped = json.ToObject<ArkadePaymentMethodConfig>(serializer);

        var option = Assert.Single(roundTripped!.SettlementOptions!);
        Assert.Equal(JTokenType.Integer, option.AdditionalData!["amount"]!.Type);
        Assert.Equal(42, option.AdditionalData["amount"]!.Value<int>());
        Assert.Equal(JTokenType.Boolean, option.AdditionalData["enabled"]!.Type);
        Assert.True(option.AdditionalData["enabled"]!.Value<bool>());
        Assert.Equal(JTokenType.Object, option.AdditionalData["nested"]!.Type);
        Assert.Equal(99, option.AdditionalData["nested"]!["key"]!.Value<int>());
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

        await Page.CheckAsync("#createNew input[name='activeSettlement'][value='bitcoin-mainchain']");
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
