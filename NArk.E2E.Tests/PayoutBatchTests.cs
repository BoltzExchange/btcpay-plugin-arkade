using System.Globalization;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Batch-path payout fulfilment through the Send wizard. A batch intent is only a
/// <i>commitment</i> to a future batch round, so a payout it fulfills must go InProgress
/// (carrying the intent tx id as proof) — never straight to Completed — and is resolved by
/// ArkPayoutSettlementListener: Completed once the batch commits, or reverted to
/// AwaitingPayment (proof cleared, retryable) when the intent is cancelled. The cancel test
/// is the regression guard for the pre-mainnet re-scan's NEW-1 finding, where cancelling a
/// payout-backed intent left the payout Completed with a blank proof and the recipient unpaid.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class PayoutBatchTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public PayoutBatchTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WizardBatchPayout_StaysInProgress_UntilBatchSettles()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var (client, storeId, payout) = await CreateApprovedPayoutWithFundsAsync();

        await SubmitBatchSendForPayoutAsync(storeId, payout);

        // The intent was only created, nothing has settled: the payout must be InProgress
        // with the intent tx id persisted as proof — Completed here is exactly the NEW-1 bug.
        var inProgress = await client.GetStorePayout(storeId, payout.Id);
        Assert.Equal(PayoutState.InProgress, inProgress.State);
        Assert.NotNull(inProgress.PaymentProof);

        // Once denigiri finalizes the batch, the settlement listener completes the payout and
        // upgrades the proof to the batch's on-chain commitment txid.
        var settled = await PollPayoutStateAsync(
            client, storeId, payout.Id, [PayoutState.Completed], TimeSpan.FromMinutes(2));
        Assert.Equal(PayoutState.Completed, settled.State);
        Assert.NotNull(settled.PaymentProof);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WizardBatchPayout_IntentCancelled_RevertsToAwaitingPayment()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var (client, storeId, payout) = await CreateApprovedPayoutWithFundsAsync();

        await SubmitBatchSendForPayoutAsync(storeId, payout);

        var inProgress = await client.GetStorePayout(storeId, payout.Id);
        Assert.Equal(PayoutState.InProgress, inProgress.State);

        // Cancel the pending intent from the Intents page (the submit lands there already).
        // The coins return to the wallet, so the settlement listener must revert the payout
        // for retry instead of leaving it marked paid with an unpaid recipient.
        Page!.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var cancelButton = Page.Locator("button[title='Cancel Intent']").First;
        await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        await cancelButton.ClickAsync();

        var reverted = await PollPayoutStateAsync(
            client, storeId, payout.Id, [PayoutState.AwaitingPayment], TimeSpan.FromMinutes(2));
        Assert.Equal(PayoutState.AwaitingPayment, reverted.State);
    }

    // --- helpers ---

    private async Task<(BTCPayServerClient Client, string StoreId, PayoutData Payout)>
        CreateApprovedPayoutWithFundsAsync()
    {
        var storeId = await CreateStoreWithArkWalletAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);

        // Recipient Arkade address harvested from a second store.
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Batch payout test",
            Amount = 0.001m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });
        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = recipientAddr,
            Amount = 0.0005m,
            PayoutMethodId = "ARKADE"
        });
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);

        // No automated processor here: approval leaves the payout AwaitingPayment for the
        // operator to pay manually through the Send wizard.
        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());
        var awaiting = await client.GetStorePayout(storeId, payout.Id);
        Assert.Equal(PayoutState.AwaitingPayment, awaiting.State);

        return (client, storeId, payout);
    }

    /// <summary>
    /// Opens the Send wizard prefilled with the payout's BIP21 (the same shape
    /// ArkPayoutHandler.InitiatePayment generates, carrying the payout id), forces the
    /// Batch spend type and submits, landing on the Intents page.
    /// </summary>
    private async Task SubmitBatchSendForPayoutAsync(string storeId, PayoutData payout)
    {
        var amountBtc = payout.OriginalAmount.ToString("0.########", CultureInfo.InvariantCulture);
        var bip21 = $"bitcoin:{payout.Destination}?amount={amountBtc}&payout={payout.Id}";

        await OpenSendForDestinationWithCoinsAsync(storeId, bip21, 60_000);

        // The prefilled destination must be recognized as payout-backed before submitting,
        // otherwise the POST would run a plain send that fulfils nothing.
        await Page!.WaitForSelectorAsync(
            ".payout-badge, .badge:has-text('Fulfilling payout')",
            new PageWaitForSelectorOptions { Timeout = 30_000 });

        await Page.Locator("#spend-type-batch").CheckAsync();
        await Page.WaitForSelectorAsync(
            "#review-content:has-text('Arkade service fee')",
            new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        var sendBtn = Page.Locator("#send-btn");
        Assert.True(await sendBtn.IsEnabledAsync(), "Send should be enabled for the batch payout spend");
        await sendBtn.ClickAsync();

        await Page.WaitForURLAsync("**/intents*", new PageWaitForURLOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// Opens the wizard with pre-selected coins and the destination prefilled, retrying when
    /// the intent scheduler reserves the polled VTXOs between the spendability check and the GET.
    /// </summary>
    private async Task OpenSendForDestinationWithCoinsAsync(string storeId, string destination, long amountSats)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var encodedDestination = Uri.EscapeDataString(destination);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var outpoints = await PollForSpendableCoinsAsync(
                storeId, "ArkAddress", amountSats, deadline - DateTimeOffset.UtcNow);
            Assert.NotEmpty(outpoints);

            var selectedVtxos = Uri.EscapeDataString(string.Join(",", outpoints));
            await GoToUrl($"/plugins/ark/stores/{storeId}/send?vtxos={selectedVtxos}&destinations={encodedDestination}");

            if (await Page!.Locator(".destination-input").CountAsync() > 0 &&
                await Page.Locator(".coin-checkbox:checked").CountAsync() > 0)
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException($"The send wizard never rendered spendable coins for store {storeId}.");
    }

    private static async Task<PayoutData> PollPayoutStateAsync(
        BTCPayServerClient client, string storeId, string payoutId, PayoutState[] expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PayoutData? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await client.GetStorePayout(storeId, payoutId);
            if (expected.Contains(latest.State))
                return latest;
            await Task.Delay(500);
        }
        return latest ?? await client.GetStorePayout(storeId, payoutId);
    }
}
