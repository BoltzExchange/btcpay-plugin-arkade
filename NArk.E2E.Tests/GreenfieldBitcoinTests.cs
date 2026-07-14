using System.Text;
using BTCPayServer.Client;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

[Collection("Arkade Plugin Tests")]
public class GreenfieldBitcoinTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public GreenfieldBitcoinTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BitcoinBip21_SendCreatesChainSwap_AndRejectsExplicitInputs()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, 200_000);

        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "BitcoinAddress", 20_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(outpoints);

        var bitcoinAddress = await GetNewRegtestBitcoinAddressAsync();
        var destination = $"bitcoin:{bitcoinAddress}?amount=0.0002";
        var authHeader = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}"))}";

        var rejectResp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/api/v1/stores/{storeId}/arkade/send").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = authHeader,
                    ["Content-Type"] = "application/json"
                },
                DataObject = new
                {
                    destination,
                    amountSats = 20_000L,
                    inputOutpoints = outpoints
                }
            });
        Assert.False(rejectResp.Ok, "Bitcoin settlement send should reject explicit inputOutpoints.");
        Assert.Contains("inputOutpoints is not supported", await rejectResp.TextAsync());

        var sendDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            await PollForSpendableCoinsAsync(
                storeId, "BitcoinAddress", 20_000, TimeSpan.FromMinutes(1));

            var sendResp = await Page.Context.APIRequest.PostAsync(
                new Uri(ServerUri!, $"/api/v1/stores/{storeId}/arkade/send").AbsoluteUri,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = authHeader,
                        ["Content-Type"] = "application/json"
                    },
                    DataObject = new
                    {
                        destination,
                        amountSats = 20_000L
                    }
                });
            if (sendResp.Ok)
                break;

            var sendError = await sendResp.TextAsync();
            Assert.Contains("VTXO_ALREADY_REGISTERED", sendError);
            if (DateTimeOffset.UtcNow > sendDeadline)
            {
                throw new TimeoutException(
                    $"The BIP21 send remained reserved by active intents: {sendError}");
            }

            await Task.Delay(3_000);
        }

        await WaitForChainSwapAsync(
            _fixture.ServerTester!.PayTester.ServiceProvider,
            walletId!,
            TimeSpan.FromMinutes(1));
    }
}
