using BTCPayServer.Client;
using BTCPayServer.Client.Models;
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

}
