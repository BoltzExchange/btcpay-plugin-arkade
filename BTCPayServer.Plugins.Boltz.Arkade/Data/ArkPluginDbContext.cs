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
    }
}
