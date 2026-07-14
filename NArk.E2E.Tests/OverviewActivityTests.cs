using System.Globalization;
using BTCPayServer.Client;
using BTCPayServer.Tests;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Covers the overview's recent-activity feed for wallet events without an
/// invoice or swap record: a manual send, the matching manual receive on the
/// recipient store, and a pending boarding deposit.
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
    public async Task OverviewRecentActivity_ShowsBoardingAndManualPayments()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var senderStoreId = await CreateStoreWithArkWalletAsync();
        var senderWalletId = await GetStoreWalletIdAsync(senderStoreId) ??
            throw new InvalidOperationException("Sender store has no Arkade wallet.");
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

        // Boarding: fund a manual boarding address on-chain, then sync until
        // the pending boarding entry shows up. Sync once BEFORE funding so
        // NBXplorer tracks the address — it only indexes transactions seen
        // after tracking starts.
        var boardingAddress = await DeriveManualBoardingAddressAsync(_fixture.ServerTester!, senderWalletId);
        await SyncWalletAsync(senderStoreId);
        await RunBitcoinCliAsync("sendtoaddress", boardingAddress, "0.001");
        await RunBitcoinCliAsync("-generate", "1");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            await SyncWalletAsync(senderStoreId);
            await GoToUrl($"/plugins/ark/stores/{senderStoreId}/overview");
            var boardingRow = Page.Locator(
                "[data-testid='activity-row']:has([data-testid='activity-title']:has-text('Boarding'))").First;
            if (await boardingRow.CountAsync() > 0 && await boardingRow.IsVisibleAsync())
            {
                var badge = await boardingRow.Locator("[data-testid='activity-badge']").InnerTextAsync();
                Assert.Contains(badge.Trim(), new[] { "Pending", "Completed" });
                break;
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException(
                    "Boarding activity entry did not appear after funding the boarding address.");
            // Each round already costs a sync round-trip plus a page load.
            await Task.Delay(500);
        }
    }

    private async Task SyncWalletAsync(string storeId)
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/sync-wallet").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = ""
            });
    }

    private static async Task<string> DeriveManualBoardingAddressAsync(
        ServerTester serverTester, string walletId)
    {
        var services = serverTester.PayTester.ServiceProvider;
        var contractService = services.GetRequiredService<IContractService>();
        var clientTransport = services.GetRequiredService<IClientTransport>();

        var terms = await clientTransport.GetServerInfoAsync();
        var contract = (ArkBoardingContract)await contractService.DeriveContract(
            walletId,
            NextContractPurpose.Boarding,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = "manual" });
        return contract.GetOnchainAddress(terms.Network).ToString();
    }

    private static async Task RunBitcoinCliAsync(params string[] args)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(new[]
            {
                "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123"
            }.Concat(args).ToArray())
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli {string.Join(' ', args)} failed: {result.StandardError.Trim()}");
    }
}
