using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NArk.E2E.Tests;

[Trait("Category", "Unit")]
public class UsdSettlementSwapCreationTests
{
    [Fact]
    public async Task StalePairHash_RequotesAndCreatesTheSwap()
    {
        var client = new CreateRecordingClient
        {
            Errors = [StalePairHash()]
        };

        var created = await CreateReverseSwap(client);

        Assert.Equal("native-1", created.SwapId);
        Assert.Equal(2, client.Attempts);
    }

    [Fact]
    public async Task StalePairHash_StopsRequotingAfterTheAttemptBudget()
    {
        var client = new CreateRecordingClient
        {
            Errors = [StalePairHash(), StalePairHash(), StalePairHash()]
        };

        var ex = await Assert.ThrowsAsync<BindingException.Operation>(
            () => CreateReverseSwap(client));

        Assert.Contains("invalid pair hash", ex.message);
        Assert.Equal(3, client.Attempts);
    }

    [Fact]
    public async Task OtherApiFailures_AreNotRequoted()
    {
        var client = new CreateRecordingClient
        {
            Errors = [new BindingException.Operation("api", "API error: {\"error\":\"pair not found\"} (code: Some(404))")]
        };

        await Assert.ThrowsAsync<BindingException.Operation>(() => CreateReverseSwap(client));

        Assert.Equal(1, client.Attempts);
    }

    private static Task<BindingCreatedSwap> CreateReverseSwap(IBoltzClient client) =>
        CompositeUsdSettlementService.CreateReverseSwap(
            client,
            new SettlementTransferRequest(
                "wallet-1",
                12_000,
                SettlementDestination.Stablecoin("Ethereum", "USDC", "0x0123456789abcdef"),
                "store-1"),
            "transfer-1",
            BindingAsset.Usdc,
            10_000,
            NullLogger.Instance,
            CancellationToken.None);

    private static BindingException.Operation StalePairHash() =>
        new("api", "API error: {\"error\":\"invalid pair hash\"} (code: Some(400))");

    private sealed class CreateRecordingClient : IBoltzClient
    {
        public IReadOnlyList<BindingException> Errors { get; init; } = [];
        public int Attempts { get; private set; }

        public Task<BindingCreatedSwap> CreateReverseSwapFromSats(
            string destination,
            string chain,
            BindingAsset asset,
            ulong invoiceAmountSats)
        {
            if (Attempts++ < Errors.Count)
                throw Errors[Attempts - 1];

            return Task.FromResult(new BindingCreatedSwap(
                SwapId: "native-1",
                Invoice: "lnbc1invoice",
                InvoiceAmountSats: invoiceAmountSats,
                OutputAmount: 10_000,
                BoltzFeeSats: 100));
        }

        public Task AcceptDegradedQuote(string swapId) => throw new NotSupportedException();

        public BindingDestination[] DestinationsAccepting(string address) =>
            throw new NotSupportedException();

        public BindingQuoteDegraded[] DrainQuoteDegradations() => throw new NotSupportedException();

        public Task<BindingSwapLimits> GetLimits() => throw new NotSupportedException();

        public Task<BindingSwap?> GetSwap(string swapId) => throw new NotSupportedException();

        public Task<ulong> ResumeSwaps() => throw new NotSupportedException();

        public Task Shutdown() => throw new NotSupportedException();
    }
}
