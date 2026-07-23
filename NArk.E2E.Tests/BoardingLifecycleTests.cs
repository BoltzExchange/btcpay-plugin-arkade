using Microsoft.Playwright;
using NArk.Tests.End2End.Common;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Boarding lifecycle: generate a boarding address on the receive page, fund it on-chain,
/// watch the deposit appear as boarding balance, then get swept by the batch pipeline into a
/// spendable offchain VTXO. At the current SDK pin a confirmed-but-unswept boarding UTXO is
/// not offchain-spendable (accepted AVL-1), so visibility and spendability are asserted as
/// separate stages on opposite sides of the sweep.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class BoardingLifecycleTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public BoardingLifecycleTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BoardingDeposit_BecomesVisible_ThenSweptSpendable()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        // Not pre-funded: any spendable offchain coin at the end can only come from the sweep.
        var storeId = await CreateStoreWithArkWalletAsync();

        await GoToUrl($"/plugins/ark/stores/{storeId}/receive");
        await Page!.ClickAsync("button[name='command'][value='generate']");
        // The Link (BIP21) tab is active by default; the boarding address lives in its own tab.
        await Page.ClickAsync("a[href='#boarding-tab']");
        await Page.WaitForSelectorAsync("#BoardingAddress",
            new PageWaitForSelectorOptions { Timeout = 30_000 });
        var boardingAddress = (await Page.Locator("#BoardingAddress").GetAttributeAsync("data-text"))
                              ?? (await Page.Locator("#BoardingAddress").InnerTextAsync()).Trim();
        Assert.False(string.IsNullOrWhiteSpace(boardingAddress), "no boarding address rendered");

        // Sync BEFORE funding: NBXplorer only indexes transactions seen after the address
        // is tracked.
        await SyncWalletAsync(storeId);

        await DockerHelper.Exec(DockerHelper.BitcoinContainer,
            [.. DockerHelper.BitcoinCliArgs, "sendtoaddress", boardingAddress, "0.001"]);
        await DockerHelper.MineBlocks(1);

        // Stage 1 — the confirmed deposit becomes visible as boarding balance. Sync
        // eagerly on every round: the deposit is already mined, so visibility only
        // needs the plugin to query NBXplorer — there is no reason to wait for the
        // periodic listeners' cadence (the sync endpoint is the same code path).
        long boardingSats = 0;
        await PollUntilAsync(async () =>
        {
            boardingSats = (await GetArkadeBalanceAsync(storeId)).BoardingSats;
            if (boardingSats == 100_000)
                return true;
            await SyncWalletAsync(storeId);
            return false;
        }, TimeSpan.FromMinutes(3),
            () => $"boarding deposit never became visible (boardingSats={boardingSats})",
            TimeSpan.FromSeconds(1));

        // Stage 2 — sweep: /suggest-coins already includes confirmed boarding UTXOs (they can
        // join a batch directly), so the balance split is the real sweep signal — boarding
        // drops to zero and available absorbs the swept amount minus batch fees. Mine between
        // polls to unstick arkd batch confirmation.
        (long AvailableSats, long BoardingSats) balance = default;
        var nextMineAt = DateTimeOffset.MinValue;
        await PollUntilAsync(async () =>
        {
            balance = await GetArkadeBalanceAsync(storeId);
            if (balance is { BoardingSats: 0, AvailableSats: >= 90_000 })
                return true;
            // Check the balance fast; keep mining at a ~2s cadence (matching
            // WaitForSwapAsync) — the batch pipeline needs occasional blocks,
            // not one per balance check.
            if (DateTimeOffset.UtcNow >= nextMineAt)
            {
                await DockerHelper.MineBlocks(1);
                nextMineAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            }
            return false;
        }, TimeSpan.FromMinutes(6),
            () => $"boarding funds never swept (boardingSats={balance.BoardingSats}, availableSats={balance.AvailableSats})",
            TimeSpan.FromMilliseconds(500));

        // Only post-sweep coins exist now, so spendability proves the swept VTXO itself.
        var outpoints = await PollForSpendableCoinsAsync(
            storeId, "ArkAddress", 90_000, TimeSpan.FromMinutes(2));
        Assert.NotEmpty(outpoints);
    }

    private Task SyncWalletAsync(string storeId) => PostPluginFormAsync(storeId, "sync-wallet");
}
