using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;

/// <summary>
/// EF-backed <see cref="SwapStorage"/> for one Arkade wallet's native Boltz
/// client. The rust core invokes these methods synchronously from its own
/// blocking-worker threads, so everything here uses the synchronous EF APIs,
/// never calls back into the owning <c>BoltzClient</c>, and surfaces every
/// failure as the generated binding store error.
/// </summary>
public sealed class EfSwapStorage(
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    string walletId) : SwapStorage
{
    // The FFI callback signature is fixed by the generated bindings; status is
    // a projection of swapJson and is deliberately not persisted.
    public void UpsertSwap(string swapId, string swapJson, string status, bool isTerminal)
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            // Native upsert so concurrent writes for one swap can never race a
            // Find/Add pair into a PK violation. The parameter arrives as text
            // and must be cast explicitly into the jsonb column.
            db.Database.ExecuteSql($"""
                INSERT INTO "BTCPayServer.Plugins.Boltz.Arkade"."NativeSwaps"
                    ("WalletId", "SwapId", "Json", "IsTerminal")
                VALUES ({walletId}, {swapId}, {swapJson}::jsonb, {isTerminal})
                ON CONFLICT ("WalletId", "SwapId")
                DO UPDATE SET "Json" = excluded."Json",
                              "IsTerminal" = excluded."IsTerminal"
                """);
        }
        catch (Exception ex)
        {
            throw StoreError("upsert_swap", ex);
        }
    }

    public string? GetSwap(string swapId)
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.NativeSwaps.Find(walletId, swapId)?.Json;
        }
        catch (Exception ex)
        {
            throw StoreError("get_swap", ex);
        }
    }

    public string[] ListActiveSwaps()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.NativeSwaps.AsNoTracking()
                .Where(swap => swap.WalletId == walletId && !swap.IsTerminal)
                .Select(swap => swap.Json)
                .ToArray();
        }
        catch (Exception ex)
        {
            throw StoreError("list_active_swaps", ex);
        }
    }

    public uint NextKeyIndex()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            // Strictly monotonic per wallet in a single atomic statement: a
            // read-modify-write could regress the counter under concurrency
            // and re-derive used preimages, enabling fund theft.
            var index = db.Database.SqlQuery<long>($"""
                INSERT INTO "BTCPayServer.Plugins.Boltz.Arkade"."NativeKeyIndices" ("WalletId", "NextIndex")
                VALUES ({walletId}, 1)
                ON CONFLICT ("WalletId")
                DO UPDATE SET "NextIndex" = "NativeKeyIndices"."NextIndex" + 1
                RETURNING "NextIndex" - 1 AS "Value"
                """)
                .AsEnumerable()
                .Single();
            return checked((uint)index);
        }
        catch (Exception ex)
        {
            throw StoreError("next_key_index", ex);
        }
    }

    private BindingException StoreError(string operation, Exception ex) =>
        new BindingException.Operation(
            "store",
            $"native swap store {operation} failed for wallet {walletId}: {ex.Message}");
}
