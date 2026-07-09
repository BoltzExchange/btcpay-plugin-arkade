using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>BTCPay pull-payment coverage for the ARKADE payout method.</summary>
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PayBitcoinPayout_CreatesChainSwap_WithValidProof()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();

        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Bitcoin payout chain swap test",
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

        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest { Revision = payout.Revision });
        var approved = await client.GetStorePayout(storeId, payout.Id);
        Assert.Equal(PayoutState.AwaitingPayment, approved.State);

        await PayArkadeInvoiceAsync(client, storeId, 200_000);

        await PollForSpendableCoinsAsync(
            storeId, "BitcoinAddress", 20_000, TimeSpan.FromMinutes(5));

        var payoutDestination = $"bitcoin:{bitcoinAddress}?amount=0.0002&payout={payout.Id}";
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/spend").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"Destination={Uri.EscapeDataString(payoutDestination)}"
            });

        var body = await resp.TextAsync();
        Assert.True(resp.Ok, $"spend returned {resp.Status}: {body}");
        Assert.False(
            body.Contains("Payment failed", StringComparison.OrdinalIgnoreCase),
            $"spend returned a failure page: {body[..Math.Min(body.Length, 1_000)]}");

        await WaitForChainSwapAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider,
            walletId!,
            TimeSpan.FromMinutes(1));

        // A bitcoin destination settles through an Ark->BTC chain swap, so paying the payout only
        // *initiates* the swap — the funds are not delivered yet. The payout must therefore be
        // InProgress (carrying the swap's transfer id), not Completed: marking it paid before the
        // swap settles would report undelivered funds as paid. Advancing InProgress -> Completed on
        // settlement is handled by the (follow-up) reconciler.
        var settling = await PollPayoutAsync(client, storeId, payout.Id, PayoutState.InProgress, TimeSpan.FromSeconds(30));
        Assert.Equal(PayoutState.InProgress, settling.State);
        Assert.NotNull(settling.PaymentProof);
        var proofType = ProofValue(settling.PaymentProof, "proofType");
        Assert.Equal("PayoutProofArk", proofType);

        var transactionId = ProofValue(settling.PaymentProof, "transactionId");
        var transferId = ProofValue(settling.PaymentProof, "transferId");
        Assert.True(
            !string.IsNullOrWhiteSpace(transferId) ||
            (!string.IsNullOrWhiteSpace(transactionId) && transactionId != new string('0', 64)),
            $"payment proof should contain a non-zero transactionId or transferId: {settling.PaymentProof}");
    }

    private static async Task<PayoutData> PollPayoutAsync(
        BTCPayServerClient client,
        string storeId,
        string payoutId,
        PayoutState expectedState,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PayoutData? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await client.GetStorePayout(storeId, payoutId);
            if (latest.State == expectedState && latest.PaymentProof is not null)
                return latest;

            await Task.Delay(1_000);
        }

        return latest ?? await client.GetStorePayout(storeId, payoutId);
    }

    private static string? ProofValue(JToken proof, string name)
    {
        if (proof is not JObject obj)
            return null;

        if (obj.TryGetValue(name, StringComparison.InvariantCultureIgnoreCase, out var value) &&
            value is not JObject and not JArray)
            return value.Value<string>();

        foreach (var property in obj.Properties())
        {
            var nested = ProofValue(property.Value, name);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }
}
