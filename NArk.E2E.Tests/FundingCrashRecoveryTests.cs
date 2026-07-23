using System.Reflection;
using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Helpers;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// Live-host coverage for the only ambiguous stablecoin funding window. The
/// tests create the real native and NNark swap records, then seed the durable
/// ledger state a process crash would leave behind instead of killing the test
/// host mid-write.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class FundingCrashRecoveryTests : PlaywrightBaseTest
{
    private const string DestinationAddress = "0xa0Ee7A142d267C1f36714E4a8F75612F20a79720";
    private const long SourceAmountSats = 40_000;
    private const int SlippageBps = 100;
    private const string SchedulerTypeName =
        "BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement.SettlementSchedulerService";
    private const string StablecoinClientTypeName =
        "BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin.IStablecoinSwapClient";

    private readonly SharedPluginTestFixture _fixture;

    public FundingCrashRecoveryTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnfundedCrash_EscalatesAfterGrace_AndCancelUnblocksWallet()
    {
        await InitializeStablecoinHostAsync();
        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetRequiredWalletIdAsync(storeId);

        await ConfigureStablecoinSettlementAsync(storeId);

        var expired = await CreateRegisteredTransferAsync(storeId, walletId);
        var dbOptions = CreateDbOptions();

        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var row = await db.UsdSettlementTransfers.SingleAsync(x => x.Id == expired.TransferId);
            row.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
            await db.SaveChangesAsync();
        }

        QueueWallet(walletId);
        var review = await WaitForTransferStateAsync(
            expired.TransferId, UsdSettlementState.ManualReview, TimeSpan.FromSeconds(35));
        Assert.Contains("inspect NNark VTXOs/intents", review.Error, StringComparison.Ordinal);

        // ManualReview remains a scheduler blocker, but it is outside the
        // database's automation-owned uniqueness constraint. Seed a second
        // recovery row directly to exercise the independent in-grace branch.
        var withinGrace = await CreateRegisteredTransferAsync(storeId, walletId);

        // The next persisted FundingStarted row is still inside the recovery
        // grace. Run a wallet pass explicitly and prove it performs no write
        // at all: UpdatedAt is the grace clock and must remain unchanged.
        UsdSettlementTransferEntity freshBefore;
        await using (var db = new ArkPluginDbContext(dbOptions))
            freshBefore = await db.UsdSettlementTransfers.AsNoTracking()
                .SingleAsync(x => x.Id == withinGrace.TransferId);

        await InvokeTaskResultAsync(GetScheduler(), "ProcessWallet", walletId, CancellationToken.None);

        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var freshAfter = await db.UsdSettlementTransfers.AsNoTracking()
                .SingleAsync(x => x.Id == withinGrace.TransferId);
            Assert.Equal(UsdSettlementState.FundingStarted, freshAfter.State);
            Assert.Equal(freshBefore.UpdatedAt, freshAfter.UpdatedAt);
            Assert.True(await HasBlockingTransferAsync(db, walletId));
        }

        // Remove the independent in-grace blocker first. The reviewed row is
        // then the only thing reserving the wallet, so the new-settlement
        // assertion below is attributable to its Greenfield dismissal.
        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var fresh = await db.UsdSettlementTransfers.SingleAsync(x => x.Id == withinGrace.TransferId);
            fresh.State = UsdSettlementState.Cancelled;
            fresh.Error = "Test cleanup after verifying the in-grace branch.";
            fresh.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var swapStorage = _fixture.ServerTester!.PayTester.ServiceProvider.GetRequiredService<ISwapStorage>();
        Assert.True(await swapStorage.UpdateSwapStatus(
            walletId, expired.NnarkSwapId, ArkSwapStatus.Failed, "Verified unfunded by crash-recovery E2E"));
        Assert.True(await swapStorage.UpdateSwapStatus(
            walletId, withinGrace.NnarkSwapId, ArkSwapStatus.Failed, "Verified unfunded by crash-recovery E2E"));

        await using (var db = new ArkPluginDbContext(dbOptions))
            Assert.True(await HasBlockingTransferAsync(db, walletId));

        var cancelled = await SendGreenfieldAsync(
            "POST",
            $"/api/v1/stores/{storeId}/arkade/stablecoin/transfers/{expired.TransferId}/cancel");
        Assert.True(cancelled.Ok, $"cancel failed: {await cancelled.TextAsync()}");
        using (var document = JsonDocument.Parse(await cancelled.TextAsync()))
        {
            Assert.Equal("cancelled", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(review.Error, document.RootElement.GetProperty("message").GetString());
        }

        await using (var db = new ArkPluginDbContext(dbOptions))
            Assert.False(await HasBlockingTransferAsync(db, walletId));

        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
        await PayArkadeInvoiceAsync(client, storeId, SourceAmountSats);
        QueueWallet(walletId);

        await PollUntilAsync(async () =>
        {
            await using var db = new ArkPluginDbContext(dbOptions);
            return await db.UsdSettlementTransfers.AnyAsync(x =>
                x.WalletId == walletId &&
                x.Id != expired.TransferId &&
                x.Id != withinGrace.TransferId &&
                x.State != UsdSettlementState.Cancelled);
        }, TimeSpan.FromMinutes(2), "wallet stayed blocked after dismissing the ManualReview row");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FundedCrash_SelfResolves_WithoutSecondFunding()
    {
        await InitializeStablecoinHostAsync();
        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetRequiredWalletIdAsync(storeId);
        var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);

        await PayArkadeInvoiceAsync(client, storeId, 100_000);
        await PollForSpendableCoinsAsync(
            storeId, "LightningInvoice", SourceAmountSats, TimeSpan.FromMinutes(10));

        var crash = await CreateRegisteredTransferAsync(storeId, walletId);
        var dbOptions = CreateDbOptions();

        // Seed the crash clock before broadcasting: funding is in flight while
        // the only durable ledger fact is a stale FundingStarted row. The
        // reconciler either escalates it (swap still unpaid past the grace) or
        // advances it once the swap reports InvoicePaid — both resolve to
        // Completed without a second funding.
        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var row = await db.UsdSettlementTransfers.SingleAsync(x => x.Id == crash.TransferId);
            row.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
            await db.SaveChangesAsync();
        }

        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var swapManagement = services.GetRequiredService<SwapsManagementService>();
        // Run a scheduler wallet pass during the crash window and prove it
        // never double-funds: the FundingStarted row reserves the wallet.
        var fundingTask = swapManagement.PayExistingSubmarineSwap(
            walletId, crash.NnarkSwapId, CancellationToken.None);
        await InvokeTaskResultAsync(GetScheduler(), "ProcessWallet", walletId, CancellationToken.None);

        var fundingTxId = await fundingTask;
        Assert.NotEqual(uint256.Zero, fundingTxId);

        var swapStorage = services.GetRequiredService<ISwapStorage>();
        var swap = (await swapStorage.GetSwaps(swapIds: [crash.NnarkSwapId])).Single();
        var vtxoStorage = services.GetRequiredService<IVtxoStorage>();
        await PollUntilAsync(async () =>
        {
            var vtxos = await vtxoStorage.GetVtxos(
                walletIds: [walletId],
                scripts: [swap.ContractScript],
                includeSpent: true);
            return vtxos.Count(vtxo => (long)vtxo.Amount == swap.ExpectedAmount) == 1;
        }, TimeSpan.FromSeconds(30), "the funded crash did not produce exactly one canonical lockup VTXO");

        await WaitForTransferStateFromApiAsync(
            storeId, crash.TransferId, "completed", TimeSpan.FromMinutes(6));

        await using (var db = new ArkPluginDbContext(dbOptions))
            Assert.Equal(1, await db.UsdSettlementTransfers.CountAsync(x => x.WalletId == walletId));

        var canonicalVtxos = await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            scripts: [swap.ContractScript],
            includeSpent: true);
        Assert.Single(canonicalVtxos, vtxo => (long)vtxo.Amount == swap.ExpectedAmount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManualReview_OverviewJourney_ShowsDetailsAndDismissesRecord()
    {
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);

        var storeId = await CreateStoreWithArkWalletAsync();
        var walletId = await GetRequiredWalletIdAsync(storeId);
        var dbOptions = CreateDbOptions();
        var reviewId = $"ui-review-{Guid.NewGuid():N}";
        const string reviewError = "Inspect NNark VTXOs/intents before dismissing this settlement.";

        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            db.UsdSettlementTransfers.Add(SeedDisplayTransfer(
                reviewId, storeId, walletId, UsdSettlementState.ManualReview, reviewError, old));
            for (var i = 0; i < 5; i++)
            {
                db.UsdSettlementTransfers.Add(SeedDisplayTransfer(
                    $"ui-completed-{Guid.NewGuid():N}",
                    storeId,
                    walletId,
                    UsdSettlementState.Completed,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-i)));
            }
            await db.SaveChangesAsync();
        }

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var reviewRow = Page!.Locator("[data-testid='activity-row']")
            .Filter(new LocatorFilterOptions { HasText = reviewError });
        await Assertions.Expect(reviewRow).ToHaveCountAsync(1);
        await Assertions.Expect(reviewRow.Locator("[data-testid='activity-badge']"))
            .ToHaveTextAsync("Needs attention");

        await reviewRow.Locator("[data-testid='activity-detail-toggle']").ClickAsync();
        await Assertions.Expect(reviewRow.Locator("[data-testid='activity-detail-text']"))
            .ToBeVisibleAsync();
        await Assertions.Expect(reviewRow.Locator("[data-testid='activity-detail-text']"))
            .ToContainTextAsync(reviewError);

        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await reviewRow.Locator("[data-testid='activity-cancel-btn']").ClickAsync();
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assertions.Expect(Page.Locator(".alert-success"))
            .ToContainTextAsync("Settlement record dismissed");

        var cancelledRow = Page.Locator("[data-testid='activity-row']")
            .Filter(new LocatorFilterOptions { HasText = reviewError });
        await Assertions.Expect(cancelledRow).ToHaveCountAsync(1);
        await Assertions.Expect(cancelledRow.Locator("[data-testid='activity-badge']"))
            .ToHaveTextAsync("Cancelled");
        await Assertions.Expect(cancelledRow.Locator("[data-testid='activity-cancel-btn']"))
            .ToHaveCountAsync(0);

        await using (var db = new ArkPluginDbContext(dbOptions))
        {
            var cancelledAt = await db.UsdSettlementTransfers
                .Where(transfer => transfer.Id == reviewId)
                .Select(transfer => transfer.UpdatedAt)
                .SingleAsync();
            for (var i = 1; i <= 5; i++)
            {
                db.UsdSettlementTransfers.Add(SeedDisplayTransfer(
                    $"ui-completed-{Guid.NewGuid():N}",
                    storeId,
                    walletId,
                    UsdSettlementState.Completed,
                    null,
                    cancelledAt.AddSeconds(i)));
            }
            await db.SaveChangesAsync();
        }

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        await Assertions.Expect(Page.Locator("[data-testid='activity-row']")
                .Filter(new LocatorFilterOptions { HasText = reviewError }))
            .ToHaveCountAsync(0);
    }

    private async Task InitializeStablecoinHostAsync()
    {
        await ArbitrumForkHelper.AssertForkReadyAsync();
        await ArbitrumForkHelper.EnsureBackendTbtcLiquidityAsync();
        _fixture.Initialize(this);
        await InitializePlaywrightAndRegisterAdminAsync(_fixture.ServerTester!);
    }

    private async Task<string> GetRequiredWalletIdAsync(string storeId)
    {
        var walletId = await GetStoreWalletIdAsync(storeId);
        Assert.False(string.IsNullOrWhiteSpace(walletId));
        return walletId!;
    }

    private DbContextOptions<ArkPluginDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<ArkPluginDbContext>()
            .UseNpgsql(_fixture.ServerTester!.PayTester.Postgres)
            .Options;

    private async Task<RegisteredTransfer> CreateRegisteredTransferAsync(
        string storeId,
        string walletId)
    {
        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var swapManagement = services.GetRequiredService<SwapsManagementService>();
        var quote = await swapManagement.GetQuoteAsync(
            new SwapRoute(SwapAsset.ArkBtc, SwapAsset.BtcLightning),
            SourceAmountSats,
            CancellationToken.None);
        var invoiceAmountSats = quote.DestinationAmount - 1;
        Assert.True(invoiceAmountSats > 0);

        var stablecoinClient = GetRuntimePluginService(StablecoinClientTypeName);
        var nativeClient = await InvokeTaskResultAsync(
            stablecoinClient,
            "GetClient",
            walletId,
            CancellationToken.None) ?? throw new InvalidOperationException("GetClient returned null");

        var destinations = (Array)(Invoke(nativeClient, "DestinationsAccepting", DestinationAddress)
            ?? throw new InvalidOperationException("DestinationsAccepting returned null"));
        var destination = destinations.Cast<object>().Single(candidate =>
            GetProperty<string>(candidate, "ChainLabel") == "Arbitrum One" &&
            GetProperty<object>(candidate, "Asset").ToString()!.StartsWith("Usdt", StringComparison.Ordinal));
        var bindingAsset = GetProperty<object>(destination, "Asset");

        var prepareMethod = FindMethod(nativeClient, "PrepareFromSats", 5);
        var nullableSlippage = Activator.CreateInstance(
            prepareMethod.GetParameters()[4].ParameterType,
            checked((uint)SlippageBps));
        var prepared = await AwaitTaskResultAsync(prepareMethod.Invoke(nativeClient,
            [DestinationAddress, "Arbitrum One", bindingAsset, checked((ulong)invoiceAmountSats), nullableSlippage]))
            ?? throw new InvalidOperationException("PrepareFromSats returned null");
        var created = await InvokeTaskResultAsync(nativeClient, "CreateReverseSwap", prepared)
            ?? throw new InvalidOperationException("CreateReverseSwap returned null");

        var rustSwapId = GetProperty<string>(created, "SwapId");
        var invoice = GetProperty<string>(created, "Invoice");
        var parsedInvoice = Bolt11Helper.Parse(invoice, Network.RegTest);
        var nnarkSwapId = await swapManagement.InitiateSubmarineSwap(
            walletId, parsedInvoice, autoPay: false, CancellationToken.None);
        var transferId = $"crash-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var transfer = new UsdSettlementTransferEntity
        {
            Id = transferId,
            StoreId = storeId,
            WalletId = walletId,
            State = UsdSettlementState.FundingStarted,
            DestinationNetwork = "Arbitrum One",
            DestinationAsset = "USDT",
            DestinationAddress = DestinationAddress,
            SourceAmountSats = SourceAmountSats,
            InvoiceAmountSats = checked((long)GetProperty<ulong>(prepared, "InvoiceAmountSats")),
            ExpectedOutputAtomic = checked((long)GetProperty<ulong>(prepared, "OutputAmount")),
            SlippageBps = SlippageBps,
            RustSwapId = rustSwapId,
            Invoice = invoice,
            PaymentHash = parsedInvoice.Hash.ToString(),
            NnarkSwapId = nnarkSwapId,
            StableLegFeeSats = checked((long)GetProperty<ulong>(prepared, "BoltzFeeSats")),
            ArkLegFeeSats = quote.TotalFees,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using (var db = new ArkPluginDbContext(CreateDbOptions()))
        {
            db.UsdSettlementTransfers.Add(transfer);
            await db.SaveChangesAsync();
        }

        return new RegisteredTransfer(transferId, nnarkSwapId);
    }

    private object GetRuntimePluginService(string typeName)
    {
        var services = _fixture.ServerTester!.PayTester.ServiceProvider;
        var scheduler = services.GetServices<IHostedService>()
            .Single(service => service.GetType().FullName == SchedulerTypeName);
        var runtimeType = scheduler.GetType().Assembly.GetType(typeName, throwOnError: true)!;
        return services.GetRequiredService(runtimeType);
    }

    private void QueueWallet(string walletId)
    {
        Invoke(GetScheduler(), "QueueWallet", walletId);
    }

    private object GetScheduler() =>
        _fixture.ServerTester!.PayTester.ServiceProvider.GetServices<IHostedService>()
            .Single(service => service.GetType().FullName == SchedulerTypeName);

    private static object? Invoke(object target, string methodName, params object?[] arguments) =>
        FindMethod(target, methodName, arguments.Length).Invoke(target, arguments);

    private static async Task<object?> InvokeTaskResultAsync(
        object target,
        string methodName,
        params object?[] arguments) =>
        await AwaitTaskResultAsync(Invoke(target, methodName, arguments));

    private static async Task<object?> AwaitTaskResultAsync(object? value)
    {
        if (value is not Task task)
            throw new InvalidOperationException("Reflected method did not return a Task");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static MethodInfo FindMethod(object target, string methodName, int argumentCount) =>
        target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == methodName && method.GetParameters().Length == argumentCount);

    private static T GetProperty<T>(object target, string propertyName) =>
        (T)(target.GetType().GetProperty(propertyName)?.GetValue(target)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{propertyName} was null"));

    private async Task<UsdSettlementTransferEntity> WaitForTransferStateAsync(
        string transferId,
        UsdSettlementState state,
        TimeSpan timeout)
    {
        UsdSettlementTransferEntity? result = null;
        await PollUntilAsync(async () =>
        {
            await using var db = new ArkPluginDbContext(CreateDbOptions());
            var row = await db.UsdSettlementTransfers.AsNoTracking()
                .SingleAsync(x => x.Id == transferId);
            if (row.State != state)
                return false;
            result = row;
            return true;
        }, timeout, $"transfer {transferId} did not reach {state}");
        return result!;
    }

    private async Task<JsonElement> WaitForTransferStateFromApiAsync(
        string storeId,
        string transferId,
        string state,
        TimeSpan timeout)
    {
        JsonElement? result = null;
        string? lastState = null;
        await PollUntilAsync(async () =>
        {
            await DockerHelper.MineBlocks();
            var response = await SendGreenfieldAsync(
                "GET", $"/api/v1/stores/{storeId}/arkade/stablecoin/transfers");
            if (!response.Ok)
                return false;
            using var document = JsonDocument.Parse(await response.TextAsync());
            foreach (var transfer in document.RootElement.EnumerateArray())
            {
                if (transfer.GetProperty("id").GetString() != transferId)
                    continue;
                lastState = transfer.GetProperty("status").GetString();
                if (lastState == state)
                {
                    result = transfer.Clone();
                    return true;
                }
            }
            return false;
        }, timeout, () => $"transfer {transferId} did not reach {state} (last: {lastState ?? "missing"})",
            TimeSpan.FromSeconds(2));
        return result!.Value;
    }

    private async Task ConfigureStablecoinSettlementAsync(string storeId)
    {
        var response = await SendGreenfieldAsync(
            "PUT",
            $"/api/v1/stores/{storeId}/arkade/stablecoin/settlement",
            new
            {
                enabled = true,
                thresholdSats = 30_000L,
                destinationChain = "arbitrum",
                destinationAddress = DestinationAddress,
                asset = "USDT",
                slippageBps = SlippageBps,
            });
        Assert.True(response.Ok, $"enabling stablecoin settlement failed: {await response.TextAsync()}");
    }

    private static Task<bool> HasBlockingTransferAsync(ArkPluginDbContext db, string walletId) =>
        db.UsdSettlementTransfers
            .Where(UsdSettlementReconciliationService.BlockingScope)
            .AnyAsync(transfer => transfer.WalletId == walletId);

    private static UsdSettlementTransferEntity SeedDisplayTransfer(
        string id,
        string storeId,
        string walletId,
        UsdSettlementState state,
        string? error,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = id,
            StoreId = storeId,
            WalletId = walletId,
            State = state,
            DestinationNetwork = "Arbitrum One",
            DestinationAsset = "USDT",
            DestinationAddress = DestinationAddress,
            SourceAmountSats = SourceAmountSats,
            InvoiceAmountSats = SourceAmountSats - 1_000,
            ExpectedOutputAtomic = 40_000_000,
            SlippageBps = SlippageBps,
            Error = error,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };

    private sealed record RegisteredTransfer(string TransferId, string NnarkSwapId);
}
