using System.Globalization;
using BTCPayServer.Client;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Covers the overview's recent-activity feed for wallet events without an
/// invoice or swap record: a manual send and the matching manual receive on
/// the recipient store. (The boarding activity entry is asserted inside
/// BoardingLifecycleTests, which already funds a boarding address.)
/// </summary>
[Collection("Arkade Plugin Tests")]
public class OverviewActivityTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public OverviewActivityTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverviewRecentActivity_ShowsManualPayments()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var senderStoreId = await CreateStoreWithArkWalletAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, senderStoreId, 250_000);

        // Recipient store — its manual receive address makes the transfer an
        // invoice-less receive on the other side.
        var recipientStoreId = await CreateStoreWithArkWalletAsync();
        var recipientAddr = await GetStoreReceiveAddressAsync(_fixture.ServerTester!, recipientStoreId);

        var outpoints = await PollForSpendableCoinsAsync(
            senderStoreId, "ArkAddress", 40_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(outpoints);

        // The intent scheduler can re-lock freshly settled coins between the spendability
        // poll and the send, so retry the send until the success redirect renders.
        var sendDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            await GoToUrl($"/plugins/ark/stores/{senderStoreId}/overview");
            var token = (await GetAntiforgeryTokenAsync()) ?? "";
            var sendResp = await Page!.Context.APIRequest.PostAsync(
                new Uri(ServerUri!, $"/plugins/ark/stores/{senderStoreId}/send").AbsoluteUri,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["RequestVerificationToken"] = token,
                        ["Content-Type"] = "application/x-www-form-urlencoded"
                    },
                    Data = "CoinSelectionMode=auto&SpendType=Arkade" +
                           string.Concat(outpoints.Select(o => $"&selectedVtxoOutpoints={Uri.EscapeDataString(o)}")) +
                           $"&Outputs[0].Destination={Uri.EscapeDataString(recipientAddr)}" +
                           $"&Outputs[0].AmountBtc={(40_000 / 100_000_000m).ToString(CultureInfo.InvariantCulture)}"
                });
            Assert.True(sendResp.Ok, $"send returned {sendResp.Status}");
            var sendBody = await sendResp.TextAsync();
            if (sendBody.Contains("Transaction sent successfully"))
                break;

            if (DateTimeOffset.UtcNow > sendDeadline)
                throw new TimeoutException("Send kept failing; coins were never spendable long enough.");
            await Task.Delay(500);
            outpoints = await PollForSpendableCoinsAsync(
                senderStoreId, "ArkAddress", 40_000, TimeSpan.FromMinutes(1));
        }

        await WaitForVisibleSelectorAsync(
            $"/plugins/ark/stores/{senderStoreId}/overview",
            "[data-testid='activity-title']:has-text('Payment sent')");
        await WaitForVisibleSelectorAsync(
            $"/plugins/ark/stores/{recipientStoreId}/overview",
            "[data-testid='activity-title']:has-text('Payment received')");
    }
}
