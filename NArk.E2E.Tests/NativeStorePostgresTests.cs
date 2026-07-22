using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// Postgres-backed coverage for the plugin's native swap store and the
/// settlement ledger's xmin concurrency token — behavior that only exists on a
/// real Postgres (atomic upsert counters, system-column row versions), so no
/// in-memory provider can stand in. Uses the same Postgres server as the
/// shared BTCPay fixture (TESTS_POSTGRES, provided by the regtest stack) but a
/// throwaway database, so these run under `make test` without starting BTCPay
/// itself. One database (created + migrated once by the class fixture) serves
/// every test — they touch disjoint wallet/transfer ids.
/// </summary>
public sealed class NativeStorePostgresFixture : IAsyncLifetime
{
    // Mirrors the Makefile's TESTS_POSTGRES for the regtest stack's Postgres.
    private const string DefaultConnectionString =
        "User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=btcpayserver";

    private readonly string _databaseName = $"arkade_native_store_{Guid.NewGuid():N}";
    private string _adminConnectionString = "";
    private string _testConnectionString = "";
    private TestDbContextFactory? _factory;

    public IDbContextFactory<ArkPluginDbContext> Factory =>
        _factory ?? throw new InvalidOperationException("fixture not initialized");

    public async Task InitializeAsync()
    {
        var serverConnectionString =
            Environment.GetEnvironmentVariable("TESTS_POSTGRES") ?? DefaultConnectionString;
        // The configured database (e.g. btcpayserver) may not exist yet — the
        // BTCPay fixture creates its own randomized one — so administrative
        // CREATE/DROP DATABASE goes through the maintenance database.
        _adminConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = "postgres"
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        _testConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<ArkPluginDbContext>()
            .UseNpgsql(_testConnectionString)
            .Options;
        _factory = new TestDbContextFactory(options);

        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        _factory = null;
        // Only this database's pool: the shared BTCPay fixture may be running
        // in parallel against the same server.
        using (var poolKey = new NpgsqlConnection(_testConnectionString))
            NpgsqlConnection.ClearPool(poolKey);
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    internal sealed class TestDbContextFactory(DbContextOptions<ArkPluginDbContext> options)
        : IDbContextFactory<ArkPluginDbContext>
    {
        public ArkPluginDbContext CreateDbContext() => new(options);
    }
}

public sealed class NativeStorePostgresTests(NativeStorePostgresFixture fixture)
    : IClassFixture<NativeStorePostgresFixture>
{
    private readonly IDbContextFactory<ArkPluginDbContext> _factory = fixture.Factory;

    [Fact]
    [Trait("Category", "Integration")]
    public void NextKeyIndex_StartsAtZeroAndIsIndependentPerWallet()
    {
        var walletA = new EfSwapStorage(_factory, "wallet-a");
        Assert.Equal(0u, walletA.NextKeyIndex());
        Assert.Equal(1u, walletA.NextKeyIndex());

        // A different wallet's counter starts fresh and never disturbs the first.
        var walletB = new EfSwapStorage(_factory, "wallet-b");
        Assert.Equal(0u, walletB.NextKeyIndex());
        Assert.Equal(2u, walletA.NextKeyIndex());
        Assert.Equal(1u, walletB.NextKeyIndex());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NextKeyIndex_IsStrictlyMonotonicAndGapFreeUnderConcurrency()
    {
        // A regressed or duplicated index re-derives an already-used preimage,
        // so concurrent allocation must never yield gaps or repeats.
        const int callers = 32;
        const int callsPerCaller = 8;
        var storage = new EfSwapStorage(_factory, "wallet-concurrent");

        var sequences = await Task.WhenAll(Enumerable
            .Range(0, callers)
            .Select(_ => Task.Run(() =>
            {
                var sequence = new List<uint>(callsPerCaller);
                for (var call = 0; call < callsPerCaller; call++)
                    sequence.Add(storage.NextKeyIndex());
                return sequence;
            })));

        foreach (var sequence in sequences)
        {
            for (var i = 1; i < sequence.Count; i++)
                Assert.True(
                    sequence[i] > sequence[i - 1],
                    $"caller sequence regressed: {sequence[i - 1]} then {sequence[i]}");
        }

        var allIndices = sequences.SelectMany(sequence => sequence).OrderBy(index => index).ToArray();
        Assert.Equal(
            Enumerable.Range(0, callers * callsPerCaller).Select(index => (uint)index).ToArray(),
            allIndices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StaleXminWrite_IsRejectedWithConcurrencyException()
    {
        var transferId = $"transfer-{Guid.NewGuid():N}";
        await using (var seed = _factory.CreateDbContext())
        {
            seed.UsdSettlementTransfers.Add(NewTransfer(transferId));
            await seed.SaveChangesAsync();
        }

        await using var firstContext = _factory.CreateDbContext();
        await using var secondContext = _factory.CreateDbContext();
        var winner = await firstContext.UsdSettlementTransfers
            .SingleAsync(transfer => transfer.Id == transferId);
        var loser = await secondContext.UsdSettlementTransfers
            .SingleAsync(transfer => transfer.Id == transferId);

        winner.State = UsdSettlementState.FundingStarted;
        await firstContext.SaveChangesAsync();

        // The second context still carries the pre-update xmin; its UPDATE must
        // match zero rows and surface as a concurrency conflict, never as a
        // silent lost update.
        loser.State = UsdSettlementState.Cancelled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => secondContext.SaveChangesAsync());
    }

    private static UsdSettlementTransferEntity NewTransfer(string id) =>
        new()
        {
            Id = id,
            StoreId = "store-1",
            WalletId = "wallet-1",
            State = UsdSettlementState.PreFunding,
            DestinationNetwork = "Arbitrum One",
            DestinationAsset = "USDT",
            DestinationAddress = "0x0123456789abcdef",
            SourceAmountSats = 40_000,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
