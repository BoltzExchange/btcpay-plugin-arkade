using System.Numerics;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Tests.End2End.Common;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// End-to-end coverage for native stablecoin settlement against the regtest
/// stack's Arbitrum-mainnet-fork anvil, modeled after boltz-web-app's
/// e2e/arbitrum specs: fund the backend's TBTC lockup wallet by impersonating
/// a whale on the fork, drive the real composed Ark → Lightning → TBTC → USDT
/// flow, and assert on-chain outcomes. The Direct (Arbitrum One) route
/// completes entirely on the fork including delivery; bridged OFT/CCTP routes
/// have no relayer or attester between the regtest chains, so — exactly like
/// the web-app's OFT spec — they are asserted up to the source-chain
/// boundary: the transfer reaches BridgeSettling and the bridge send/burn
/// event is on the fork. The fixture's ARKSTABLECOIN* endpoint overrides opt
/// the plugin out of its mainnet-only default and point the native client at
/// the local stack.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class StablecoinSettlementTests : PlaywrightBaseTest
{
    // Anvil default account 9 — an EOA the flow never spends from, so its USDT
    // balance only moves when settlement delivers.
    private const string DestinationAddress = "0xa0Ee7A142d267C1f36714E4a8F75612F20a79720";

    private const string StablecoinRadio = "input[name='activeSettlement'][value='usd']";
    private const string DestinationAddressInput =
        "input[name='SettlementInputs[Usd].Data[destinationAddress]']";
    private const string DestinationChainSelect =
        "select[name='SettlementInputs[Usd].Data[destinationChain]']";
    private const string AssetSelect = "select[name='SettlementInputs[Usd].Data[asset]']";
    private const string ThresholdInput = "input[name='SettlementInputs[Usd].Data[thresholdSats]']";

    private readonly SharedPluginTestFixture _fixture;

    public StablecoinSettlementTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Settings_ActivatesStablecoinSettlement_WithEndpointOverrides()
    {
        await ArbitrumForkHelper.AssertForkReadyAsync();

        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");

        // The endpoint overrides make the stablecoin method selectable off-mainnet.
        var radio = Page!.Locator(StablecoinRadio);
        await Assertions.Expect(radio).ToBeEnabledAsync();
        await radio.CheckAsync();

        // Select the network before the address: the wallet-connect script
        // clears the address input on every network change.
        await Page.SelectOptionAsync(DestinationChainSelect, "Arbitrum One");
        await Page.SelectOptionAsync(AssetSelect, "USDT");
        await Page.FillAsync(DestinationAddressInput, DestinationAddress);
        await Page.FillAsync(ThresholdInput, "30000");
        await Page.ClickAsync("[data-testid='settlement-save-btn']");
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        var errorAlerts = Page.Locator(".alert-danger");
        if (await errorAlerts.CountAsync() > 0)
        {
            var fragments = new List<string>();
            for (var i = 0; i < await errorAlerts.CountAsync(); i++)
                fragments.Add(await errorAlerts.Nth(i).InnerHTMLAsync());
            Assert.Fail($"settlement save failed: {string.Join(" | ", fragments)}");
        }

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");
        await Assertions.Expect(Page.Locator(StablecoinRadio)).ToBeCheckedAsync();
        Assert.Equal(DestinationAddress, await Page.InputValueAsync(DestinationAddressInput));
        Assert.Equal("Arbitrum One", await Page.InputValueAsync(DestinationChainSelect));
        Assert.Equal("USDT", await Page.InputValueAsync(AssetSelect));
        Assert.Equal("30000", await Page.InputValueAsync(ThresholdInput));

        var configured = await SendGreenfieldAsync(
            "GET", $"/api/v1/stores/{storeId}/arkade/stablecoin/settlement");
        Assert.True(configured.Ok, $"config lookup failed: {await configured.TextAsync()}");
        using (var configuredDocument = JsonDocument.Parse(await configured.TextAsync()))
        {
            Assert.True(configuredDocument.RootElement.GetProperty("enabled").GetBoolean());
            Assert.True(configuredDocument.RootElement.GetProperty("available").GetBoolean());
            Assert.Equal("arbitrum", configuredDocument.RootElement.GetProperty("destinationChain").GetString());
        }

        var disabled = await SendGreenfieldAsync(
            "PUT",
            $"/api/v1/stores/{storeId}/arkade/stablecoin/settlement",
            new { enabled = false });
        Assert.True(disabled.Ok, $"disabling stablecoin settlement failed: {await disabled.TextAsync()}");
        using var disabledDocument = JsonDocument.Parse(await disabled.TextAsync());
        Assert.False(disabledDocument.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(DestinationAddress,
            disabledDocument.RootElement.GetProperty("destinationAddress").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InitialSetup_InvalidStablecoinDestination_StillCreatesWalletWithSettlementOff()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page!.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        await OpenCreateWalletSettlementStepAsync();
        await Page.Locator($"#createNew {StablecoinRadio}").CheckAsync();
        await Page.SelectOptionAsync($"#createNew {DestinationChainSelect}", "Arbitrum One");
        await Page.SelectOptionAsync($"#createNew {AssetSelect}", "USDT");
        await Page.FillAsync($"#createNew {DestinationAddressInput}", "not-a-stablecoin-address");
        await Page.FillAsync($"#createNew {ThresholdInput}", "30000");
        await Page.ClickAsync("#createNew [data-testid='create-wallet-btn']");

        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assertions.Expect(Page.Locator(".alert-warning[role='alert']")).ToContainTextAsync(
            "wallet setup succeeded, but settlement was not enabled");
        Assert.False(string.IsNullOrWhiteSpace(await GetStoreWalletIdAsync(storeId)));

        await GoToUrl($"/plugins/ark/stores/{storeId}/settings");
        await Assertions.Expect(Page.Locator(StablecoinRadio)).Not.ToBeCheckedAsync();
        await Assertions.Expect(Page.Locator("input[name='activeSettlement'][value='none']"))
            .ToBeCheckedAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WalletBalance_AboveThreshold_DeliversUsdtOnArbitrumFork()
    {
        var balanceBefore = await StartSettlementFlowAsync("Arbitrum One", "USDT", splitPayments: true);

        var transfer = await WaitForTransferStateAsync(
            LastFlowStoreId!, UsdSettlementState.Completed, TimeSpan.FromMinutes(6));

        Assert.Equal("USDT", transfer.DestinationAsset);
        Assert.Equal(DestinationAddress, transfer.DestinationAddress);
        // The threshold only gates when settlement fires; the transfer itself
        // sweeps the full available balance (both 20k-sat payments), not the
        // 30k threshold.
        Assert.Equal(40_000, transfer.SourceAmountSats);
        var delivered = transfer.DeliveredOutputAtomic.GetValueOrDefault();
        Assert.True(delivered > 0, "completed transfer reported no delivered output");
        // The durable fee facts cover both legs of the composite transfer.
        var feesPaid = transfer.StableLegFeeSats.GetValueOrDefault() +
                       transfer.ArkLegFeeSats.GetValueOrDefault();
        Assert.True(feesPaid > 0, "completed transfer reported no consolidated fees");
        Assert.False(
            string.IsNullOrEmpty(transfer.ArbitrumClaimTxHash),
            "completed transfer has no Arbitrum claim transaction");

        var balanceAfter = await ArbitrumForkHelper.GetErc20BalanceAsync(
            ArbitrumForkHelper.UsdtTokenAddress, DestinationAddress);
        Assert.Equal(new BigInteger(delivered), balanceAfter - balanceBefore);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WalletBalance_AboveThreshold_SendsOftBridgeTxForEthereumUsdt()
    {
        await StartSettlementFlowAsync("Ethereum", "USDT");
        var fromBlock = FlowStartBlock;

        // No LayerZero relayer exists between the regtest chains, so the
        // transfer rests in BridgeSettling; the send itself is on the fork.
        var transfer = await WaitForTransferStateAsync(
            LastFlowStoreId!, UsdSettlementState.BridgeSettling, TimeSpan.FromMinutes(6));

        Assert.Equal("Oft", transfer.BridgeKind);
        var bridgeRef = transfer.BridgeRef;
        Assert.False(string.IsNullOrEmpty(bridgeRef), "OFT transfer recorded no bridge reference");

        var logs = await ArbitrumForkHelper.GetLogsAsync(
            fromBlock,
            [ArbitrumForkHelper.Usdt0NativeOftAddress, ArbitrumForkHelper.Usdt0LegacyOftAddress],
            ArbitrumForkHelper.OftSentTopic);
        Assert.True(logs.Count > 0, "no OFTSent event was emitted on the fork");

        // bridgeRef is the LayerZero GUID — topics[1] of the OFTSent log.
        var guids = logs
            .Select(log => (string?)log?["topics"]?[1])
            .Where(guid => guid is not null)
            .ToList();
        Assert.Contains(bridgeRef!.ToLowerInvariant(), guids.Select(g => g!.ToLowerInvariant()));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WalletBalance_AboveThreshold_SendsCctpBurnForEthereumUsdc()
    {
        await StartSettlementFlowAsync("Ethereum", "USDC");
        var fromBlock = FlowStartBlock;

        // No CCTP attester exists between the regtest chains, so the transfer
        // rests in BridgeSettling; the burn itself is on the fork.
        var transfer = await WaitForTransferStateAsync(
            LastFlowStoreId!, UsdSettlementState.BridgeSettling, TimeSpan.FromMinutes(6));

        Assert.Equal("Cctp", transfer.BridgeKind);
        Assert.False(
            string.IsNullOrEmpty(transfer.BridgeRef),
            "CCTP transfer recorded no bridge reference");

        var logs = await ArbitrumForkHelper.GetLogsAsync(
            fromBlock,
            [ArbitrumForkHelper.CctpMessageTransmitterAddress],
            ArbitrumForkHelper.MessageSentTopic);
        Assert.True(logs.Count > 0, "no CCTP MessageSent event was emitted on the fork");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GreenfieldCancel_DismissesOnlyManualReviewTransfers()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletResponse = await SendGreenfieldAsync("GET", $"/api/v1/stores/{storeId}/arkade/wallet");
        Assert.True(walletResponse.Ok, $"wallet lookup failed: {await walletResponse.TextAsync()}");
        string walletId;
        using (var walletDocument = JsonDocument.Parse(await walletResponse.TextAsync()))
            walletId = walletDocument.RootElement.GetProperty("walletId").GetString()!;

        // Seed the ledger directly: reaching ManualReview through the UI would
        // require breaking a live swap mid-flight. The automation-owned row
        // lives on its own wallet so the happy path below can assert the real
        // wallet ends up fully unblocked. The context is built directly against
        // the server's database rather than resolved from PayTester's provider:
        // the plugin assembly lives in its own load context there, so its
        // ArkPluginDbContext registration is a different Type identity than
        // this test assembly's and GetRequiredService can never match it.
        var dbOptions = new DbContextOptionsBuilder<ArkPluginDbContext>()
            .UseNpgsql(_fixture.ServerTester!.PayTester.Postgres)
            .Options;
        var reviewTransferId = $"review-{Guid.NewGuid():N}";
        var settlingTransferId = $"settling-{Guid.NewGuid():N}";
        const string reviewError = "Ark funding returned an ambiguous result and was not retried.";
        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            db.UsdSettlementTransfers.Add(SeedTransfer(
                reviewTransferId, storeId, walletId, UsdSettlementState.ManualReview, reviewError));
            db.UsdSettlementTransfers.Add(SeedTransfer(
                settlingTransferId, storeId, $"other-{walletId}", UsdSettlementState.BridgeSettling, error: null));
            await db.SaveChangesAsync();
        }

        // Automation owns every state but ManualReview; cancelling one is a 400.
        var rejected = await SendGreenfieldAsync(
            "POST", $"/api/v1/stores/{storeId}/arkade/stablecoin/transfers/{settlingTransferId}/cancel");
        Assert.Equal(400, rejected.Status);

        var cancelled = await SendGreenfieldAsync(
            "POST", $"/api/v1/stores/{storeId}/arkade/stablecoin/transfers/{reviewTransferId}/cancel");
        Assert.True(cancelled.Ok, $"cancel failed: {await cancelled.TextAsync()}");
        using (var cancelledDocument = JsonDocument.Parse(await cancelled.TextAsync()))
        {
            Assert.Equal("cancelled", cancelledDocument.RootElement.GetProperty("status").GetString());
            // The original failure text survives as the audit trail.
            Assert.Equal(reviewError, cancelledDocument.RootElement.GetProperty("message").GetString());
        }

        // Cancelled is terminal, so the wallet leaves the scheduler's blocking
        // set (HasBlockingUsdTransfer) and future settlements can fire again.
        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            Assert.False(await db.UsdSettlementTransfers.AnyAsync(transfer =>
                transfer.WalletId == walletId &&
                transfer.State != UsdSettlementState.Completed &&
                transfer.State != UsdSettlementState.Refunded &&
                transfer.State != UsdSettlementState.Cancelled));
        }
    }

    private static UsdSettlementTransferEntity SeedTransfer(
        string id,
        string storeId,
        string walletId,
        UsdSettlementState state,
        string? error) =>
        new()
        {
            Id = id,
            StoreId = storeId,
            WalletId = walletId,
            State = state,
            DestinationNetwork = "Arbitrum One",
            DestinationAsset = "USDT",
            DestinationAddress = DestinationAddress,
            SourceAmountSats = 40_000,
            InvoiceAmountSats = 39_000,
            ExpectedOutputAtomic = 40_000_000,
            SlippageBps = 100,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private string? LastFlowStoreId { get; set; }

    private long FlowStartBlock { get; set; }

    /// <summary>
    /// Common flow preamble: fork preflight, backend TBTC liquidity, store +
    /// Ark wallet, threshold settlement config for the given route, and
    /// payments crossing the 30k threshold — two 20k-sat payments when
    /// <paramref name="splitPayments"/> (the Direct flow pins the
    /// multi-payment sweep total), one 40k payment otherwise (accumulation
    /// is the same balance sum; one invoice round is cheaper). Returns the
    /// destination's USDT balance before the flow and records the fork
    /// block the flow started at for event-window queries.
    /// </summary>
    private async Task<BigInteger> StartSettlementFlowAsync(
        string destinationChain, string asset, bool splitPayments = false)
    {
        await ArbitrumForkHelper.AssertForkReadyAsync();
        await ArbitrumForkHelper.EnsureBackendTbtcLiquidityAsync();

        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        LastFlowStoreId = await CreateStoreWithArkWalletAsync();
        FlowStartBlock = await ArbitrumForkHelper.GetBlockNumberAsync();
        var balanceBefore = await ArbitrumForkHelper.GetErc20BalanceAsync(
            ArbitrumForkHelper.UsdtTokenAddress, DestinationAddress);

        var configResponse = await SendGreenfieldAsync(
            "PUT",
            $"/api/v1/stores/{LastFlowStoreId}/arkade/stablecoin/settlement",
            new
            {
                enabled = true,
                thresholdSats = 30_000L,
                destinationChain = destinationChain switch
                {
                    "Arbitrum One" => "arbitrum",
                    "Ethereum" => "ethereum",
                    _ => destinationChain
                },
                destinationAddress = DestinationAddress,
                asset,
                slippageBps = 100,
            });
        Assert.True(configResponse.Ok, $"enabling stablecoin settlement failed: {await configResponse.TextAsync()}");

        await EnsureArkdCliReadyAsync();
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        if (splitPayments)
        {
            await PayArkadeInvoiceAsync(client, LastFlowStoreId, 20_000);
            await PayArkadeInvoiceAsync(client, LastFlowStoreId, 20_000);
        }
        else
        {
            await PayArkadeInvoiceAsync(client, LastFlowStoreId, 40_000);
        }

        return balanceBefore;
    }

    /// <summary>
    /// Poll the store's durable transfer ledger until one transfer reaches
    /// <paramref name="successState"/>, mining a bitcoin block per attempt so
    /// the LN and Ark legs keep confirming, and failing fast on any terminal
    /// failure state.
    /// </summary>
    private async Task<UsdSettlementTransferEntity> WaitForTransferStateAsync(
        string storeId, UsdSettlementState successState, TimeSpan timeout)
    {
        UsdSettlementTransferEntity? match = null;
        var lastSeen = "no transfer yet";
        var dbOptions = new DbContextOptionsBuilder<ArkPluginDbContext>()
            .UseNpgsql(_fixture.ServerTester!.PayTester.Postgres)
            .Options;
        await PollUntilAsync(async () =>
        {
            await DockerHelper.MineBlocks();
            await using var db = new ArkPluginDbContext(dbOptions);
            var transfer = await db.UsdSettlementTransfers.AsNoTracking()
                .Where(candidate => candidate.StoreId == storeId)
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync();
            if (transfer is null)
                return false;

            lastSeen = $"transfer {transfer.Id} in state {transfer.State}: {transfer.Error ?? "no error"}";
            if (transfer.State is UsdSettlementState.Refunded or
                UsdSettlementState.Cancelled or
                UsdSettlementState.ManualReview)
                Assert.Fail($"stablecoin settlement failed — {lastSeen}");
            if (transfer.State != successState)
                return false;

            match = transfer;
            return true;
        }, timeout, () => $"stablecoin settlement did not reach {successState}: {lastSeen}",
            TimeSpan.FromSeconds(2));
        return match!;
    }

}
