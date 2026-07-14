using System.Globalization;
using BTCPayServer.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Swaps.Boltz;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Drives the /send wizard with a Bitcoin on-chain destination and verifies that a bitcoin
/// address no longer forces Batch. In Arkade mode an amount within the Boltz chain-swap limits
/// settles via an Arkade→BTC chain swap (swap UX + chain-swap fee), while an amount outside those
/// limits auto-falls back to Batch (batch UX + batch fee) — matching the backend /send behavior.
/// Neither case dead-ends the user with a disabled Send.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class SendWizardBitcoinTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public SendWizardBitcoinTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SendWizard_BitcoinAddress_WithinLimits_KeepsArkadeTypeAndShowsSwapFee()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);
        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();

        await OpenSendWithSpendableCoinsAsync(storeId, 20_000);
        await Page!.FillAsync(".destination-input", bitcoinAddress);
        // 0.0002 BTC (20k sats) is within the regtest chain-swap limits (GreenfieldBitcoinTests
        // settles this exact amount via chain swap).
        await Page.FillAsync(".amount-input", "0.0002");

        // The destination badge must read as a swap (not batch-only) for an in-limits Arkade send.
        await Page.WaitForSelectorAsync(
            ".destination-type-badge:has-text('Bitcoin (Swap)')",
            new PageWaitForSelectorOptions { Timeout = 30_000 });

        // The chain-swap fee arrives via the async estimate-fees fetch and renders the
        // "x% + y sats miner fee" breakdown in the (collapsed) review section.
        await Page.WaitForSelectorAsync(
            "#review-content:has-text('miner fee')",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Arkade must stay selected and enabled — a Bitcoin address no longer forces Batch.
        var arkadeRadio = Page.Locator("#spend-type-arkade");
        Assert.True(await arkadeRadio.IsCheckedAsync(),
            "Arkade spend type should stay selected for a Bitcoin address");
        Assert.True(await arkadeRadio.IsEnabledAsync(),
            "Arkade spend type should stay enabled for a Bitcoin address");
        Assert.False(await Page.Locator("#spend-type-batch").IsCheckedAsync(),
            "Batch should not be forced for a single-output Bitcoin address");
        Assert.True(await Page.Locator("#spend-type-batch").IsEnabledAsync(),
            "Batch should remain selectable for a Bitcoin address");

        Assert.True(await Page.Locator("#send-btn").IsEnabledAsync(),
            "Send should be enabled once the swap fee estimate loads");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SendWizard_BitcoinAddress_OutsideLimits_FallsBackToBatch()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var limitsValidator = services.GetRequiredService<BoltzLimitsValidator>();
        var chainLimits = await limitsValidator.GetChainLimitsAsync(isBtcToArk: false) ??
            throw new InvalidOperationException("Boltz ARK to BTC chain limits were unavailable.");

        // One sat below the chain-swap minimum → out of limits, so the send must fall back to Batch.
        var belowMinSats = chainLimits.MinAmount - 1;
        // Fund comfortably above the send amount so the Batch estimate has coins + fee headroom.
        var fundingSats = belowMinSats + 100_000;

        var storeId = await CreateStoreWithArkWalletAsync();

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, fundingSats);
        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();
        var amountBtc = (belowMinSats / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture);

        await OpenSendWithSpendableCoinsAsync(storeId, belowMinSats);
        await Page!.FillAsync(".destination-input", bitcoinAddress);
        await Page.FillAsync(".amount-input", amountBtc);

        // Out of chain-swap limits: the badge must flip to batch settlement after the estimate.
        await Page.WaitForSelectorAsync(
            ".destination-type-badge:has-text('Bitcoin (Batch)')",
            new PageWaitForSelectorOptions { Timeout = 30_000 });

        // The batch fee (not the chain-swap fee) is shown, and it is not an error message.
        await Page.WaitForSelectorAsync(
            "#review-content:has-text('Batch transaction fee')",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // Critical: no dead-end error — Send stays enabled so the backend fallback can run.
        Assert.True(await Page.Locator("#send-btn").IsEnabledAsync(),
            "Send should stay enabled when a Bitcoin amount falls outside the chain-swap limits");

        // The Arkade radio is not disabled/forced away — the fallback is reflected in the fee/labels,
        // not by mutating the user's single-output spend-type selection.
        Assert.True(await Page.Locator("#spend-type-arkade").IsEnabledAsync(),
            "Arkade spend type should remain enabled for a single-output Bitcoin address");
        Assert.False(await Page.Locator("#spend-type-batch").IsCheckedAsync(),
            "The Batch radio should not be force-selected for a single-output Bitcoin address");
    }

    private async Task OpenSendWithSpendableCoinsAsync(string storeId, long amountSats)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var outpoints = await PollForSpendableCoinsAsync(
                storeId, "BitcoinAddress", amountSats, deadline - DateTimeOffset.UtcNow);
            Assert.NotEmpty(outpoints);

            var selectedVtxos = Uri.EscapeDataString(string.Join(",", outpoints));
            await GoToUrl($"/plugins/ark/stores/{storeId}/send?vtxos={selectedVtxos}");

            // The intent scheduler can reserve the selected VTXOs between the
            // spendability poll and this GET. Retry with the post-batch outpoints
            // instead of waiting for an input that this response cannot render.
            if (await Page!.Locator(".destination-input").CountAsync() > 0 &&
                await Page.Locator(".coin-checkbox:checked").CountAsync() > 0)
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException($"The send wizard never rendered spendable coins for store {storeId}.");
    }
}
