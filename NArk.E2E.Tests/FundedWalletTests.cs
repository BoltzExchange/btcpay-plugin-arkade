using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Consolidated funded-wallet journey. Funding once avoids repeated arkd
/// batch waits while still covering fee estimation, send, and payout approval.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class FundedWalletTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public FundedWalletTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FundedWallet_FullJourney_FundEstimateSendApprovePayout()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithSingleKeyWalletAsync();

        // Fund with two notes so a single redemption batch yields two
        // independent VTXOs — the send and the payout each take one
        // without an inter-step change-settle wait.
        var walletId = await FundStoreWalletViaNoteAsync(_fixture.ServerTester!, storeId, 250_000);
        await FundWalletViaNoteAsync(_fixture.ServerTester!, walletId, 250_000);

        // Spendability, not displayed balance, is the readiness signal.
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "ArkAddress", 40_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(outpoints);

        // Recipient store — just to harvest a valid Arkade address.
        var recipientStoreId = await CreateStoreWithSingleKeyWalletAsync();
        await GoToUrl($"/plugins/ark/stores/{recipientStoreId}/overview");
        var recipientAddr = await Page!.InputValueAsync("[data-testid='receive-address']");
        Assert.False(string.IsNullOrWhiteSpace(recipientAddr));

        // Estimate fees for an Ark to Ark transfer.
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var feeResp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/estimate-fees").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destination = recipientAddr, amountSats = 40_000L }
            });
        if (!feeResp.Ok)
            throw new InvalidOperationException(
                $"estimate-fees returned {feeResp.Status}: {await feeResp.TextAsync()}");
        var feeJson = await feeResp.JsonAsync();
        var hasFee = feeJson!.Value.TryGetProperty("feeSats", out _) ||
                     feeJson.Value.TryGetProperty("totalFeeSats", out _) ||
                     feeJson.Value.TryGetProperty("intentFeeSats", out _) ||
                     feeJson.Value.TryGetProperty("estimatedFeeSats", out _);
        Assert.True(hasFee, $"estimate-fees response missing a fee field: {feeJson}");

        // Refresh outpoints immediately before build-intent; batch settlement
        // can change coin state between the initial poll and the send.
        outpoints = await PollForSpendableCoinsAsync(
            storeId, "ArkAddress", 40_000, TimeSpan.FromMinutes(1));
        Assert.NotEmpty(outpoints);

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        token = (await GetAntiforgeryTokenAsync()) ?? "";
        var sendResp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/build-intent").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = $"StoreId={Uri.EscapeDataString(storeId)}" +
                       $"&VtxoOutpointsRaw={Uri.EscapeDataString(string.Join(",", outpoints))}" +
                       $"&Outputs[0].Destination={Uri.EscapeDataString(recipientAddr)}" +
                       $"&Outputs[0].AmountBtc={(40_000 / 100_000_000m).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            });
        Assert.True(sendResp.Ok, $"build-intent returned {sendResp.Status}");
        var sendBody = await sendResp.TextAsync();
        Assert.DoesNotContain("No valid VTXOs selected", sendBody);

        // Approval should leave the payout waiting for manual payment.
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        var pp = await client.CreatePullPayment(storeId, new CreatePullPaymentRequest
        {
            Name = "Journey payout",
            Amount = 0.002m,
            Currency = "BTC",
            PayoutMethods = ["ARKADE"]
        });
        var payout = await client.CreatePayout(pp.Id, new CreatePayoutRequest
        {
            Destination = recipientAddr,
            Amount = 0.0015m,
            PayoutMethodId = "ARKADE"
        });
        Assert.Equal(PayoutState.AwaitingApproval, payout.State);
        await client.ApprovePayout(storeId, payout.Id, new ApprovePayoutRequest());

        var approved = await client.GetStorePayout(storeId, payout.Id);
        Assert.Equal(PayoutState.AwaitingPayment, approved.State);
    }

}
