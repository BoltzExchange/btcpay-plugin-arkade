using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Boltz.Client;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Wallets;
using NArk.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Stablecoin;

/// <summary>
/// Owns one native Boltz client per Arkade wallet. Wallet-scoped clients keep
/// derived-key counters and swap secrets isolated in their own EF-backed
/// storage while remaining recoverable from the store's existing BIP-39
/// backup.
/// </summary>
public sealed class StablecoinSwapClient : IStablecoinSwapClient, IHostedService, IDisposable
{
    // Automatic settlement executes at the current market rate. Rust still
    // needs a narrow tolerance for the on-chain minOut between its live quote
    // and transaction inclusion; this is an execution detail, not a merchant
    // quote preference.
    private const uint ExecutionSlippageBps = 100;

    private const string MainnetOnlyReason = "Stablecoin settlement is mainnet-only.";
    private const string LinuxOnlyReason = "Stablecoin settlement is available only on Linux.";
    private const string SwapProviderRequiredReason = "Stablecoin settlement requires an Ark swap provider endpoint.";
    private const string StoppedReason = "The stablecoin settlement service has stopped.";
    private const string WalletSeedRequiredReason =
        "Stablecoin settlement requires a locally backed-up HD Arkade wallet seed.";

    private readonly IWalletStorage _walletStorage;
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;
    private readonly ILogger<StablecoinSwapClient> _logger;
    private readonly bool _mainnet;
    private readonly bool _linux;
    private readonly bool _hasSwapProvider;
    private readonly StablecoinEndpointOverrides _endpointOverrides;
    private readonly ConcurrentDictionary<string, Lazy<Task<WalletClient>>> _clients = new();
    private int _stopped;
    private int _disposed;

    public StablecoinSwapClient(
        IConfiguration configuration,
        BTCPayServerEnvironment serverEnvironment,
        ArkNetworkConfig arkNetworkConfig,
        IWalletStorage walletStorage,
        IDbContextFactory<ArkPluginDbContext> dbContextFactory,
        ILogger<StablecoinSwapClient> logger)
    {
        _walletStorage = walletStorage;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _mainnet = serverEnvironment.NetworkType == ChainName.Mainnet;
        _linux = OperatingSystem.IsLinux();
        _hasSwapProvider = !string.IsNullOrWhiteSpace(arkNetworkConfig.BoltzUri);
        _endpointOverrides = StablecoinEndpointOverrides.FromConfiguration(configuration);
    }

    public bool IsAvailable =>
        _linux &&
        _hasSwapProvider &&
        (_mainnet || _endpointOverrides.Active) &&
        Volatile.Read(ref _stopped) == 0;

    public bool EndpointOverridesActive => _endpointOverrides.Active;

    public string? UnavailableReason => IsAvailable
        ? null
        : !_linux
            ? LinuxOnlyReason
            : !_hasSwapProvider
                ? SwapProviderRequiredReason
                : !_mainnet && !_endpointOverrides.Active
                    ? MainnetOnlyReason
                    : StoppedReason;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IBoltzClient> GetClient(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new StablecoinSwapUnavailableException(UnavailableReason ?? StoppedReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(walletId);

        var lazy = _clients.GetOrAdd(
            walletId,
            static (id, owner) => new Lazy<Task<WalletClient>>(
                () => owner.CreateClient(id),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

        try
        {
            return (await lazy.Value.WaitAsync(cancellationToken)).Client;
        }
        catch
        {
            // Evict only a creation that itself completed unsuccessfully
            // (faulted or cancelled inside the factory). A cancelled awaiter
            // must leave a still-running factory in place: its client would
            // otherwise become an orphan with a live runtime while the next
            // call builds a duplicate for the same wallet.
            if (lazy.IsValueCreated && lazy.Value.IsCompleted && !lazy.Value.IsCompletedSuccessfully)
                _clients.TryRemove(new KeyValuePair<string, Lazy<Task<WalletClient>>>(walletId, lazy));
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        var shutdowns = _clients.Values
            .Where(lazy => lazy.IsValueCreated)
            .Select(ShutdownClient)
            .ToArray();
        await Task.WhenAll(shutdowns);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    internal static byte[] DeriveWalletSeed(string mnemonic) =>
        new Mnemonic(mnemonic, Wordlist.English).DeriveSeed();

    private async Task<WalletClient> CreateClient(string walletId)
    {
        var wallet = await _walletStorage.GetWalletById(walletId, CancellationToken.None);
        if (wallet is not { WalletType: WalletType.HD } || string.IsNullOrWhiteSpace(wallet.Secret))
            throw new StablecoinSwapUnavailableException(WalletSeedRequiredReason);

        var seed = DeriveWalletSeed(wallet.Secret);
        try
        {
            var config = new ClientConfig(
                Seed: seed,
                ReferralId: "btcpay-arkade",
                SlippageBps: ExecutionSlippageBps,
                // Apply overrides all-or-nothing: a partial configuration must
                // not silently rewire individual endpoints on mainnet.
                ApiUrl: _endpointOverrides.Active ? _endpointOverrides.ApiUrl : null,
                GasSponsorUrl: _endpointOverrides.Active ? _endpointOverrides.GasSponsorUrl : null,
                ArbitrumRpcUrl: _endpointOverrides.Active ? _endpointOverrides.ArbitrumRpcUrl : null,
                SolanaRpcUrl: _endpointOverrides.EffectiveSolanaRpcUrl,
                DisableDeliveryPolling: _endpointOverrides.ShouldDisableDeliveryPolling(_mainnet));
            var client = new BoltzClient(config, new EfSwapStorage(_dbContextFactory, walletId));

            _logger.LogInformation(
                "Initialized wallet-scoped native stablecoin client for Arkade wallet {WalletId}",
                walletId);
            return new WalletClient(client);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private async Task ShutdownClient(Lazy<Task<WalletClient>> lazy)
    {
        try
        {
            var client = await lazy.Value;
            await client.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to shut down a native stablecoin client cleanly");
        }
    }

    private sealed class WalletClient(BoltzClient client)
    {
        private int _stopped;

        public IBoltzClient Client { get; } = client;

        public async Task Shutdown()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            try
            {
                await client.Shutdown();
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}

/// <summary>
/// Optional endpoint overrides for the native stablecoin client, read from
/// configuration (settable via BTCPAY_-prefixed environment variables).
/// Overriding the Boltz API, the Arbitrum RPC, and the gas sponsor opts the
/// deployment out of the mainnet-only default so the flow can run against a
/// regtest stack whose anvil forks Arbitrum mainnet. All three are required so
/// an override deployment never silently broadcasts through the production
/// gas sponsor; for the same reason the Solana RPC (the other write-capable
/// endpoint) falls back to an unroutable address instead of the production
/// default when overrides are active. Read-only feeds (OFT deployments, CCTP
/// Iris, LayerZero scan) keep their production defaults — they have no
/// regtest equivalent and cannot move funds.
/// </summary>
public sealed record StablecoinEndpointOverrides(
    string? ApiUrl,
    string? ArbitrumRpcUrl,
    string? GasSponsorUrl,
    string? SolanaRpcUrl,
    bool DisableDeliveryPolling)
{
    public bool Active =>
        !string.IsNullOrWhiteSpace(ApiUrl) &&
        !string.IsNullOrWhiteSpace(ArbitrumRpcUrl) &&
        !string.IsNullOrWhiteSpace(GasSponsorUrl);

    public string? EffectiveSolanaRpcUrl => Active
        ? string.IsNullOrWhiteSpace(SolanaRpcUrl) ? "http://127.0.0.1:9" : SolanaRpcUrl
        : null;

    public bool ShouldDisableDeliveryPolling(bool mainnet) =>
        !mainnet && Active && DisableDeliveryPolling;

    public static StablecoinEndpointOverrides FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue<string?>("ARKSTABLECOINAPIURL"),
        configuration.GetValue<string?>("ARKSTABLECOINARBITRUMRPCURL"),
        configuration.GetValue<string?>("ARKSTABLECOINGASSPONSORURL"),
        configuration.GetValue<string?>("ARKSTABLECOINSOLANARPCURL"),
        // Bridged swaps left undeliverable by a test stack (fork identifiers
        // unknown to LayerZero scan / Circle Iris) would otherwise poll those
        // production APIs every 30s until shutdown. Test-environment knob;
        // production and mainnet-with-overrides keep delivery polling.
        configuration.GetValue<bool>("ARKSTABLECOINDISABLEDELIVERYPOLLING"));
}

public sealed class StablecoinSwapUnavailableException(string message) : InvalidOperationException(message);
