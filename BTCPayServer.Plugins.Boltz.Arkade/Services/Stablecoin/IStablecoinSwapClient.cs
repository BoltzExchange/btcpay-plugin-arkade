using Boltz.Client;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;

/// <summary>
/// Managed application boundary for the in-process Boltz stablecoin client.
/// </summary>
public interface IStablecoinSwapClient
{
    /// <summary>Whether stablecoin settlement is supported by this BTCPay network.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether explicitly configured endpoint overrides (Boltz API, Arbitrum RPC) are active.
    /// Overrides opt the deployment out of the mainnet-only default, e.g. for regtest E2E.
    /// </summary>
    bool EndpointOverridesActive { get; }

    /// <summary>A user-facing explanation when <see cref="IsAvailable"/> is <c>false</c>.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Get the native client isolated to one Arkade wallet. Its signing seed is
    /// deterministically recovered from that wallet's local BIP-39 seed.
    /// </summary>
    Task<IBoltzClient> GetClient(string walletId, CancellationToken cancellationToken = default);
}
