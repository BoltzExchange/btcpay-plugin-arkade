using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>
/// An Arkade invoice is paid (via <c>ark send</c>, like checkout cheat mode) while BTCPay is
/// DOWN; after an in-process restart the cold-start VTXO catch-up plus the invoice listener
/// must settle it without any event having fired. Uses a dedicated ServerTester — the shared
/// fixture's server must never be restarted, every other test holds its ServiceProvider —
/// inside the same xUnit collection so nothing runs in parallel with the second BTCPay host.
/// </summary>
[Collection("Arkade Plugin Tests")]
public class RestartReconciliationTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public RestartReconciliationTests(SharedPluginTestFixture fixture, ITestOutputHelper helper)
        : base(helper)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvoicePaidWhileServerDown_SettlesAfterRestart()
    {
        // Sets the process-wide Arkade env vars; the shared server itself stays untouched.
        _fixture.Initialize(this);

        var testDir = Path.Combine(Directory.GetCurrentDirectory(), "ArkadeRestartTest");
        var tester = CreateServerTester(testDir, newDb: true);
        tester.PayTester.LoadPluginsInDefaultAssemblyContext = false;

        // ServerTester's newDb only takes effect when the connection string's database is
        // literally "btcpayserver" — ours isn't, so without this override the tester would
        // share the suite database with the still-running shared server, whose VTXO listener
        // would settle the invoice during the "downtime" and make the test vacuous.
        var postgres = new Npgsql.NpgsqlConnectionStringBuilder(tester.PayTester.Postgres)
        {
            Database = $"btcpay_restart_{Random.Shared.Next(100_000_000)}"
        };
        tester.PayTester.Postgres = postgres.ToString();
        await tester.StartAsync();
        try
        {
            await InitializePlaywright(tester);
            await GoToUrl("/register");
            await RegisterNewUser(isAdmin: true);
            var storeId = await CreateStoreWithArkWalletAsync();
            await EnsureArkdCliReadyAsync();

            var client = new BTCPayServerClient(ServerUri, CreatedUser, Password);
            var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
            {
                Amount = 20_000,
                Currency = "SATS",
                Checkout = new InvoiceDataBase.CheckoutOptions { PaymentMethods = ["ARKADE"] }
            });
            var methods = await client.GetInvoicePaymentMethods(storeId, invoice.Id);
            var destination = methods.First(m => m.PaymentMethodId == "ARKADE").Destination;
            Assert.False(string.IsNullOrWhiteSpace(destination));

            // Wait until the wallet's VTXO poller has persisted its cold-start cursor
            // (vtxo.lastFullPollAt wallet metadata, written by the routine full-set poll
            // every ~5s once the cold-start catch-up succeeded) — the restart catch-up
            // queries from that timestamp, and disposing the host before the first write
            // would silently downgrade this test to the full-history path. Any persisted
            // value predates the downtime payment below, so existence is sufficient.
            var walletStorage = tester.PayTester.ServiceProvider.GetRequiredService<IWalletStorage>();
            await PollUntilAsync(async () =>
            {
                var wallets = await walletStorage.LoadAllWallets();
                return wallets.Count > 0 && wallets.All(w =>
                    w.Metadata?.ContainsKey(VtxoSynchronizationService.LastFullPollAtMetadataKey) == true);
            }, TimeSpan.FromSeconds(60),
                $"the VTXO poller never persisted {VtxoSynchronizationService.LastFullPollAtMetadataKey}",
                TimeSpan.FromMilliseconds(250));

            // Only the host is disposed; port, datadir and Postgres stay reserved on the
            // tester for the restart.
            tester.PayTester.Dispose();

            // Pay the invoice while the server is down.
            var txId = await ArkSendAsync(destination, 20_000);
            Assert.False(string.IsNullOrWhiteSpace(txId));

            await tester.PayTester.StartAsync();

            var restartedClient = new BTCPayServerClient(tester.PayTester.ServerUri, CreatedUser, Password);
            await WaitForInvoiceSettledAsync(restartedClient, storeId, invoice.Id, TimeSpan.FromMinutes(4));
        }
        finally
        {
            tester.Dispose();
        }
    }

    /// <summary>Pays an ark address from the arkd container's CLI wallet (checkout cheat-mode parity).</summary>
    private static async Task<string?> ArkSendAsync(string destination, long amountSats)
    {
        var container = await ResolveArkdContainerAsync();
        var result = await Cli.Wrap("docker")
            .WithArguments([
                "exec", container, "ark", "send",
                "--to", destination,
                "--amount", amountSats.ToString(),
                "--password", "secret"
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"ark send failed (exit={result.ExitCode}): {result.StandardError.Trim()} {result.StandardOutput.Trim()}");
        return JObject.Parse(result.StandardOutput).Value<string>("txid");
    }
}
