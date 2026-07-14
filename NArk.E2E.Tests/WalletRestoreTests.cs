using BTCPayServer.Client;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Funded-wallet restore: fund a wallet imported from a known mnemonic, clear it from its
/// store, re-import the same mnemonic into a fresh store, and assert the deterministic
/// wallet id matches and the funds come back spendable after background recovery
/// (contract re-derivation + VTXO re-sync).
/// </summary>
[Collection("Arkade Plugin Tests")]
public class WalletRestoreTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public WalletRestoreTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FundedWallet_ClearAndReimport_RecoversSpendableFunds()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

        var store1 = await CreateStoreWithArkWalletAsync(mnemonic);
        var walletId1 = await GetStoreWalletIdAsync(store1);
        Assert.False(string.IsNullOrWhiteSpace(walletId1));

        // Recovery can only rediscover what the gap-limit scan re-derives, so fund via a real
        // invoice payment and wait until the coins settle onto HD-derived contracts.
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, store1, 200_000);
        var fundedOutpoints = await PollForSpendableCoinsAsync(
            store1, "ArkAddress", 60_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(fundedOutpoints);

        // Clearing is mandatory: re-importing the same seed otherwise trips the
        // shared-wallet guard ("already in use by another store").
        var clearResp = await PostPluginFormAsync(store1, "clear-wallet");
        Assert.True(clearResp.Ok, $"clear-wallet returned {clearResp.Status}");

        var store2 = await CreateStoreWithArkWalletAsync(mnemonic);
        var walletId2 = await GetStoreWalletIdAsync(store2);
        Assert.Equal(walletId1, walletId2);

        // The restored funds must be spendable again, not merely displayed.
        var restoredOutpoints = await PollForSpendableCoinsAsync(
            store2, "ArkAddress", 60_000, TimeSpan.FromMinutes(5));
        Assert.NotEmpty(restoredOutpoints);
    }
}
