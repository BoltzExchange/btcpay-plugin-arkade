using BTCPayServer.Tests;
using NArk.Tests.End2End.Common;
using Xunit;

namespace NArk.E2E.Tests;

/// <summary>
/// xUnit collection fixture that owns the BTCPayServer lifecycle for the
/// suite. One <see cref="ServerTester"/> instance starts BTCPay (with the
/// Arkade plugin loaded via <c>appsettings.dev.json:DEBUG_PLUGINS</c>) and
/// is shared by every test in <see cref="PluginTestCollection"/>.
///
/// Pattern copied from rockstardev/BTCPayServerPlugins.RockstarDev — that
/// repo demonstrates the only known-working way to run plugin E2E against
/// a real BTCPay process: inherit BTCPay's own ServerTester rather than
/// rolling a custom host-spawn fixture.
/// </summary>
public class SharedPluginTestFixture : IDisposable
{
    public ServerTester? ServerTester { get; private set; }

    private AnvilArbSanitizingProxy? _anvilProxy;

    /// <summary>
    /// Called by every test's constructor; starts BTCPay once and reuses
    /// the same instance for every subsequent call.
    /// </summary>
    public void Initialize(PlaywrightBaseTest testInstance)
    {
        if (ServerTester is not null) return;

        // Shorten the Arkade plugin's intent-generation poll cadence for
        // the suite. Production leaves this unset (NArk's 5-min default);
        // tests that fund a wallet by importing a note need the redemption
        // intent generated within seconds, not minutes. BTCPay's config
        // reads BTCPAY_-prefixed env vars, so this lands as
        // ARKINTENTPOLLSECONDS=5 in IConfiguration. Must be set before
        // ServerTester.StartAsync builds the host config.
        Environment.SetEnvironmentVariable("BTCPAY_ARKINTENTPOLLSECONDS", "5");

        // The regtest stack's VTXOs expire sooner than the production renewal threshold.
        // Keep the fast cadence for imported/unrolled note redemption, but shorten the
        // threshold so ordinary funded VTXOs are not continuously reserved for renewal.
        Environment.SetEnvironmentVariable("BTCPAY_ARKINTENTTHRESHOLDSECONDS", "1");

        // Point the native stablecoin client at the regtest stack: Boltz API via
        // nginx, the Arbitrum-mainnet-fork anvil (through the l1BlockNumber
        // sanitizing proxy), and the local gas sponsor emulator. Overriding
        // API + RPC also opts stablecoin settlement out of its mainnet-only
        // default (the fork keeps chain id 42161, so the client's hardcoded
        // mainnet contract addresses resolve).
        _anvilProxy = new AnvilArbSanitizingProxy();
        Environment.SetEnvironmentVariable("BTCPAY_ARKSTABLECOINAPIURL", "http://localhost:9001");
        Environment.SetEnvironmentVariable("BTCPAY_ARKSTABLECOINARBITRUMRPCURL", _anvilProxy.Url);
        Environment.SetEnvironmentVariable("BTCPAY_ARKSTABLECOINGASSPONSORURL", "http://localhost:18547/alchemy");
        // Bridged test swaps stay undeliverable on the fork; without this the
        // native client would poll production LayerZero/Circle APIs with fork
        // identifiers for the rest of the suite.
        Environment.SetEnvironmentVariable("BTCPAY_ARKSTABLECOINDISABLEDELIVERYPOLLING", "true");

        var testDir = Path.Combine(Directory.GetCurrentDirectory(), "ArkadePluginTests");
        ServerTester = testInstance.CreateServerTester(testDir, newDb: true);
        PlaywrightBaseTest.WriteArkNetworkOverride(testDir);
        // Load plugins in an isolated AssemblyLoadContext — matches the
        // production load model AND matches rockstardev's reference
        // fixture.
        ServerTester.PayTester.LoadPluginsInDefaultAssemblyContext = false;

        // Hard 3-min ceiling on BTCPay startup. Past iterations of this
        // suite saw a 20-min CI timeout because BTCPay's host gets stuck
        // somewhere downstream of the plugin's Execute (likely a hosted
        // service / IStartupTask in NArk.Core). Failing fast surfaces
        // the real signal instead of timing the GitHub Actions job out.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            ServerTester.StartAsync().WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "BTCPay startup didn't complete within 3 minutes. The plugin's hosted services or IStartupTask are likely blocking. Run the test locally with debugger attached to inspect.");
        }
    }

    public void Dispose()
    {
        ServerTester?.Dispose();
        ServerTester = null;
        _anvilProxy?.Dispose();
        _anvilProxy = null;
    }
}

[CollectionDefinition("Arkade Plugin Tests")]
public class PluginTestCollection : ICollectionFixture<SharedPluginTestFixture>
{
    // Marker class — xUnit discovers [CollectionDefinition] + ICollectionFixture<>.
}
