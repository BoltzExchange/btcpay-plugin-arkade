using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.Boltz.Arkade.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWalletEntity> Wallets { get; set; }
    public DbSet<ArkWalletContractEntity> WalletContracts { get; set; }
    public DbSet<VtxoEntity> Vtxos { get; set; }
    public DbSet<ArkSwapEntity> Swaps { get; set; }
    public DbSet<ArkIntentEntity> Intents { get; set; }
    public DbSet<ArkIntentVtxoEntity> IntentVtxos { get; set; }
    public DbSet<UsdSettlementTransferEntity> UsdSettlementTransfers { get; set; }
    public DbSet<NativeSwapEntity> NativeSwaps { get; set; }
    public DbSet<NativeKeyIndexEntity> NativeKeyIndices { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Boltz.Arkade";
        });
        modelBuilder.Entity<ArkWalletEntity>()
            .Property(w => w.AccountDescriptor)
            .HasDefaultValue(null);
        modelBuilder.Entity<ArkWalletContractEntity>()
            .HasIndex(c => c.ActivityState);
        modelBuilder.Entity<UsdSettlementTransferEntity>(entity =>
        {
            entity.ToTable("UsdSettlementTransfers", "BTCPayServer.Plugins.Boltz.Arkade");
            entity.HasKey(transfer => transfer.Id);
            entity.Property(transfer => transfer.Xmin).IsRowVersion();
            entity.Property(transfer => transfer.State).HasConversion<string>();
            entity.HasIndex(transfer => new { transfer.WalletId, transfer.State });
            entity.HasIndex(transfer => transfer.RustSwapId)
                .IsUnique()
                .HasFilter("\"RustSwapId\" IS NOT NULL");
            entity.HasIndex(transfer => transfer.NnarkSwapId)
                .IsUnique()
                .HasFilter("\"NnarkSwapId\" IS NOT NULL");
        });

        modelBuilder.Entity<NativeSwapEntity>(entity =>
        {
            entity.ToTable("NativeSwaps", "BTCPayServer.Plugins.Boltz.Arkade");
            entity.HasKey(swap => new { swap.WalletId, swap.SwapId });
            entity.Property(swap => swap.Json).HasColumnType("jsonb");
            entity.HasIndex(swap => new { swap.WalletId, swap.IsTerminal });
        });
        modelBuilder.Entity<NativeKeyIndexEntity>(entity =>
        {
            entity.ToTable("NativeKeyIndices", "BTCPayServer.Plugins.Boltz.Arkade");
            entity.HasKey(row => row.WalletId);
        });
    }
}
