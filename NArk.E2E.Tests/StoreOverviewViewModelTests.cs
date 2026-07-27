using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore.Entities;
using NArk.Swaps.Models;
using Xunit;

namespace NArk.E2E.Tests;

[Trait("Category", "Unit")]
public class StoreOverviewViewModelTests
{
    [Fact]
    public void ShouldWarnWalletBackup_RequiresCurrentWalletFunds()
    {
        var model = new StoreOverviewViewModel
        {
            WalletType = WalletType.HD,
            SignerAvailable = true,
            WalletBackedUp = false,
            HasSecret = true
        };

        Assert.False(model.ShouldWarnWalletBackup);

        model.HasCurrentWalletFunds = true;
        Assert.True(model.ShouldWarnWalletBackup);

        model.WalletBackedUp = true;
        Assert.False(model.ShouldWarnWalletBackup);
    }

    [Fact]
    public void ShouldWarnWalletBackup_IgnoresStorePaymentHistoryWithoutCurrentWalletFunds()
    {
        var model = new StoreOverviewViewModel
        {
            WalletType = WalletType.HD,
            SignerAvailable = true,
            WalletBackedUp = false,
            HasSecret = true,
            RecentPayments =
            [
                new RecentPaymentViewModel
                {
                    Title = "Payment received",
                    PaymentStatus = PaymentStatus.Settled
                }
            ],
            PaymentStats =
            [
                new StoreOverviewStatViewModel { Name = "Total volume", Value = 50_000 }
            ]
        };

        Assert.False(model.ShouldWarnWalletBackup);
    }
}

/// <summary>
/// The overview's "In progress" stat counts pending Lightning swaps minus the
/// ones a stablecoin settlement owns. Ownership is a property of the transfer
/// row, not of its state: a Cancelled transfer still owns whatever NNark
/// submarine swap it created, and that swap is settlement plumbing rather than
/// user Lightning activity. Runs against real Postgres because the exclusion is
/// entirely a SQL question — the counting query lives inline in the overview
/// action, so this pins the query semantics it depends on.
/// </summary>
public sealed class StoreOverviewActivityQueryTests(NativeStorePostgresFixture fixture)
    : IClassFixture<NativeStorePostgresFixture>
{
    private readonly IDbContextFactory<ArkPluginDbContext> _factory = fixture.Factory;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancelledTransfersSwap_IsNotCountedAsPendingLightning()
    {
        const string storeId = "store-overview";
        var walletId = $"wallet-{Guid.NewGuid():N}";
        var swapId = $"swap-{Guid.NewGuid():N}";
        var contractScript = $"script-{Guid.NewGuid():N}";

        await using (var seed = _factory.CreateDbContext())
        {
            seed.Wallets.Add(new ArkWalletEntity { Id = walletId });
            seed.WalletContracts.Add(new ArkWalletContractEntity
            {
                Script = contractScript,
                WalletId = walletId,
                Type = "ArkPaymentContract"
            });
            seed.Swaps.Add(new ArkSwapEntity
            {
                SwapId = swapId,
                WalletId = walletId,
                SwapType = ArkSwapType.Submarine,
                Status = ArkSwapStatus.Pending,
                ContractScript = contractScript,
                Invoice = "lnbcrt1settlement",
                ExpectedAmount = 40_000
            });
            seed.UsdSettlementTransfers.Add(new UsdSettlementTransferEntity
            {
                Id = $"transfer-{Guid.NewGuid():N}",
                StoreId = storeId,
                WalletId = walletId,
                State = UsdSettlementState.Cancelled,
                NnarkSwapId = swapId,
                DestinationNetwork = "Arbitrum One",
                DestinationAsset = "USDT",
                DestinationAddress = "0x0123456789abcdef",
                SourceAmountSats = 40_000,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateDbContext();
        var stablecoinSwapIds = await db.UsdSettlementTransfers
            .Where(transfer => transfer.StoreId == storeId &&
                transfer.WalletId == walletId &&
                transfer.NnarkSwapId != null)
            .Select(transfer => transfer.NnarkSwapId!)
            .ToListAsync();

        // The terminal-state filter the overview uses elsewhere would drop this
        // row, which is exactly how the swap used to leak into the stat.
        Assert.Contains(swapId, stablecoinSwapIds);

        var lightningSwapTypes = new[] { ArkSwapType.ReverseSubmarine, ArkSwapType.Submarine };
        var pendingLightningSwapCount = await db.Swaps
            .Where(s => s.WalletId == walletId && lightningSwapTypes.Contains(s.SwapType))
            .Where(s => (s.Status == ArkSwapStatus.Pending || s.Status == ArkSwapStatus.Unknown) &&
                !stablecoinSwapIds.Contains(s.SwapId))
            .CountAsync(s => s.SwapType == ArkSwapType.Submarine ||
                db.Vtxos.Any(v => v.Script == s.ContractScript));

        Assert.Equal(0, pendingLightningSwapCount);
    }
}
