using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using NArk.Abstractions.Wallets;
using Xunit;

namespace NArk.E2E.Tests;

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
                new StoreOverviewStatViewModel { Name = "Recent volume", Value = 50_000 }
            ]
        };

        Assert.False(model.ShouldWarnWalletBackup);
    }
}
