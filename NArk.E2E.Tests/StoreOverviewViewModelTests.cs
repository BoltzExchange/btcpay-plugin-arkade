using BTCPayServer.Plugins.ArkPayServer.Models;
using NArk.Abstractions.Wallets;
using Xunit;

namespace NArk.E2E.Tests;

public class StoreOverviewViewModelTests
{
    [Fact]
    public void ShouldWarnWalletBackup_RequiresReceivedFunds()
    {
        var model = new StoreOverviewViewModel
        {
            WalletType = WalletType.HD,
            CanManagePrivateKeys = true,
            SignerAvailable = true,
            WalletBackedUp = false,
            Wallet = "seed"
        };

        Assert.False(model.ShouldWarnWalletBackup);

        model.HasReceivedFunds = true;
        Assert.True(model.ShouldWarnWalletBackup);

        model.WalletBackedUp = true;
        Assert.False(model.ShouldWarnWalletBackup);
    }
}
