using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Services;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

public class StoreOverviewViewModel
{
    public string? StoreId { get; set; }
    public bool IsLightningEnabled { get; set; }
    public ArkBalancesViewModel? Balances { get; set; }
    public string? WalletId { get; set; }
    public bool SignerAvailable { get; set; }
    public bool HasSecret { get; set; }
    public bool AllowSubDustAmounts { get; set; }
    public bool WalletBackedUp { get; set; }
    public bool HasCurrentWalletFunds { get; set; }

    public WalletType WalletType { get; set; }

    /// <summary>Status of the most recent background wallet-recovery run (import or Rescan), if any.</summary>
    public RecoveryStatus? RecoveryStatus { get; set; }

    public bool ShouldWarnWalletBackup =>
        WalletType == WalletType.HD &&
        SignerAvailable &&
        !WalletBackedUp &&
        HasCurrentWalletFunds &&
        HasSecret;

    public string? ArkOperatorUrl { get; set; }
    public bool ArkOperatorConnected { get; set; }
    public string? ArkOperatorError { get; set; }

    public string? BoltzUrl { get; set; }
    public bool BoltzConnected { get; set; }
    public string? BoltzError { get; set; }

    public long? BoltzReverseMinAmount { get; set; }
    public long? BoltzReverseMaxAmount { get; set; }
    public decimal? BoltzReverseFeePercentage { get; set; }
    public long? BoltzReverseMinerFee { get; set; }

    public long? BoltzSubmarineMinAmount { get; set; }
    public long? BoltzSubmarineMaxAmount { get; set; }
    public decimal? BoltzSubmarineFeePercentage { get; set; }
    public long? BoltzSubmarineMinerFee { get; set; }

    public int TotalVtxoCount { get; set; }
    public int TotalIntentCount { get; set; }
    public int TotalSwapCount { get; set; }

    public bool HasPaymentServiceIssue =>
        !ArkOperatorConnected || (IsLightningEnabled && !string.IsNullOrEmpty(BoltzUrl) && !BoltzConnected);


    public List<StoreOverviewStatViewModel> PaymentStats { get; set; } = [];
    public List<RecentPaymentViewModel> RecentPayments { get; set; } = [];
}

public class RecentPaymentViewModel
{
    public DateTimeOffset Date { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "BTC";
    public string? AmountPrefix { get; set; }
    public string? AmountSubtext { get; set; }
    public bool AmountSubtextSensitive { get; set; }
    public bool ShowAmount { get; set; } = true;
    public bool IsOutgoing { get; set; }
    public PaymentStatus? PaymentStatus { get; set; }
    public ArkSwapStatus? SwapStatus { get; set; }

    /// <summary>Overrides the badge derived from <see cref="PaymentStatus"/>/<see cref="SwapStatus"/> (e.g. boarding rows).</summary>
    public string? BadgeLabel { get; set; }
    public string? BadgeClass { get; set; }
}

public class StoreOverviewStatViewModel
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
    public StoreOverviewStatUnit Unit { get; set; }
}

public enum StoreOverviewStatUnit
{
    None,
    Sats
}
