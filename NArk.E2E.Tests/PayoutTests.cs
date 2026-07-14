using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using NArk.Swaps.Models;
using NArk.Tests.End2End.Common;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// BTCPay pull-payment coverage for the ARKADE payout method — the full lifecycle:
/// pull-payment creation, claim/approval transitions, and end-to-end fulfilment by the
/// automated payout processor (which settles payouts internally via ArkadeSpendingService,
/// with no controller round-trip): an Ark→Ark payout Completes; a bitcoin payout goes
/// InProgress behind an ARK→BTC chain swap.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class PayoutTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public PayoutTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    /// <summary>ARKADE is offered as a pull-payment payout method.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreatePullPayment_WithArkadeMethod_Succeeds()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Arkade payout test",
            Amount = 0.001m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });

        Assert.False(string.IsNullOrEmpty(pp.Id));
    }

    /// <summary>Claiming to an Arkade address leaves the payout awaiting approval.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClaimPayout_ToArkAddress_AwaitingApproval()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        // Harvest a real Arkade address from a second store.
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Claim test",
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

        Assert.False(string.IsNullOrEmpty(payout.Id));
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);
    }

    /// <summary>
    /// An approved Ark→Ark payout is settled by the automated processor to a real txid,
    /// completing the payout internally with no manual /send action.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AutomatedPayout_ArkAddress_ProcessorCompletesInternally()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        // Fund via a settled invoice payment — a real VTXO carrying the server key. A raw imported
        // note (ArkNoteContract) has a null server key and can't be checkpoint-spent offchain, so
        // the processor's auto coin selection would fail on it ("Server key is required…").
        await PayArkadeInvoiceAsync(client, storeId, 200_000);
        var spendable = await PollForSpendableCoinsAsync(
            storeId, "ArkAddress", 60_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(spendable);

        // Recipient Arkade address harvested from a second store.
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        await EnableArkAutomatedPayoutProcessorAsync(storeId);

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Automated ark payout",
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

        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

        // The processor picks it up on approval (ProcessNewPayoutsInstantly) and settles the
        // Ark→Ark transfer to a real txid, completing the payout entirely on its own. Freshly
        // funded VTXOs sit in a pending batch intent, so the first ticks hit VTXO_ALREADY_REGISTERED
        // and the spend only succeeds once that intent settles (~a batch). A settled production
        // balance completes in one tick; the generous window here absorbs the regtest batch wait.
        var settled = await PollPayoutStateAsync(
            client, storeId, payout.Id, [PayoutState.Completed], TimeSpan.FromMinutes(8));
        Assert.Equal(PayoutState.Completed, settled.State);
        Assert.NotNull(settled.PaymentProof);
    }

    /// <summary>
    /// An approved bitcoin-destination payout is settled by the automated processor through an
    /// ARK→BTC chain swap. Because a swap only <i>initiates</i> delivery, the payout goes
    /// InProgress (carrying the swap id) and a chain swap must be recorded;
    /// ArkPayoutSettlementListener later completes it once the swap settles, so a fast regtest
    /// settlement may already show Completed by the time we observe it.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AutomatedPayout_BitcoinDestination_ProcessorInitiatesChainSwap()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);
        var spendable = await PollForSpendableCoinsAsync(
            storeId, "BitcoinAddress", 20_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(spendable);

        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();

        await EnableArkAutomatedPayoutProcessorAsync(storeId);

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Automated bitcoin payout",
            Amount = 0.0003m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });
        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = $"bitcoin:{bitcoinAddress}",
            Amount = 0.0002m,
            PayoutMethodId = "ARKADE"
        });
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);

        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

        // A freshly funded VTXO may be registered by the intent scheduler between the
        // spendability check and the processor tick. The processor leaves the payout
        // AwaitingPayment and retries after that intent's batch settles, matching the
        // Ark-address payout flow above.
        var settling = await PollPayoutStateAsync(
            client, storeId, payout.Id,
            [PayoutState.InProgress, PayoutState.Completed], TimeSpan.FromMinutes(8));
        Assert.True(settling.State is PayoutState.InProgress or PayoutState.Completed,
            $"expected the chain-swap payout to be InProgress (or Completed after settlement), got {settling.State}");
        Assert.NotNull(settling.PaymentProof);

        // Completion: the chain swap settles, the settlement listener finishes the payout,
        // and BTC actually arrives at the destination.
        await WaitForSwapAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider,
            walletId!,
            ArkSwapType.ChainArkToBtc,
            [ArkSwapStatus.Settled],
            TimeSpan.FromMinutes(3),
            mineWhileWaiting: true);

        var completed = await PollPayoutStateAsync(
            client, storeId, payout.Id, [PayoutState.Completed], TimeSpan.FromMinutes(2));
        Assert.Equal(PayoutState.Completed, completed.State);

        var receivedSats = await DockerHelper.BitcoinGetReceivedByAddressSats(bitcoinAddress, minConf: 0);
        Assert.True(receivedSats > 0,
            $"payout chain swap settled but no BTC arrived at {bitcoinAddress}");
    }

    // --- helpers ---

    /// <summary>
    /// Enables the ARKADE automated payout processor for the store the same way the plugin UI does:
    /// a form POST to the store's ark-automated processor endpoint. ProcessNewPayoutsInstantly makes
    /// an approval trigger an immediate processing tick; IntervalMinutes=1 is the fallback cadence.
    /// </summary>
    private async Task EnableArkAutomatedPayoutProcessorAsync(string storeId)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/stores/{storeId}/payout-processors/ark-automated").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = "ProcessNewPayoutsInstantly=true&IntervalMinutes=1"
            });
        Assert.True(resp.Ok, $"configure payout processor returned {resp.Status}: {await resp.TextAsync()}");
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
