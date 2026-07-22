using BTCPayServer;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NArk.Tests.End2End.Common;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NArk.E2E.Tests;

/// <summary>Playwright helpers for Arkade plugin E2E tests.</summary>
public abstract class PlaywrightBaseTest : UnitTestBase, IDisposable
{
    protected PlaywrightBaseTest(ITestOutputHelper helper) : base(helper)
    {
    }

    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }
    public IPage? Page { get; private set; }
    public Uri? ServerUri { get; private set; }
    public string? CreatedUser { get; private set; }
    public string? Password { get; private set; }
    private IServiceProvider? Services { get; set; }

    /// <summary>
    /// Points the plugin at the BoltzExchange/regtest endpoints through the
    /// datadir <c>ark.json</c> override (merged over the SDK's Regtest preset
    /// by <c>ArkPlugin.GetNetworkConfig</c>), so the NNark preset — which
    /// still describes the ArkLabs stack — stays untouched. Must be written
    /// after <c>CreateServerTester</c> (its constructor wipes the test
    /// directory) and before <c>StartAsync</c>.
    /// </summary>
    public static void WriteArkNetworkOverride(string testDir)
    {
        var datadir = Path.Combine(testDir, "pay");
        Directory.CreateDirectory(datadir);
        File.WriteAllText(Path.Combine(datadir, "ark.json"),
            """
            {
                "boltz": "http://localhost:9001/",
                "esplora": "http://localhost:3002",
                "electrum-tcp": "tcp://localhost:19001"
            }
            """);
    }

    private static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static readonly SemaphoreSlim _arkdCliSetupLock = new(1, 1);
    private static bool _arkdCliReady;

    /// <summary>Starts a Chromium browser and opens a page pointed at the running BTCPay.</summary>
    protected async Task InitializePlaywright(ServerTester serverTester)
    {
        Services = serverTester.PayTester.ServiceProvider;
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = true,
            SlowMo = IsRunningInCI ? 100 : 50
        };
        if (serverTester.PayTester.InContainer)
        {
            launchOptions.Args = new[]
            {
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-gpu"
            };
        }
        Browser = await Playwright.Chromium.LaunchAsync(launchOptions);

        var context = await Browser.NewContextAsync();
        Page = await context.NewPageAsync();
        Page.SetDefaultTimeout(15000);
        ServerUri = serverTester.PayTester.ServerUri;
        TestLogs.LogInformation($"Playwright: Browsing to {ServerUri}");
    }

    protected async Task InitializePlaywrightAndRegisterAdminAsync(ServerTester serverTester)
    {
        await InitializePlaywright(serverTester);
        await GoToUrl("/register");
        await RegisterNewUser(isAdmin: true);
    }

    protected async Task GoToUrl(string relativeUrl)
    {
        ArgumentNullException.ThrowIfNull(Page);
        ArgumentNullException.ThrowIfNull(ServerUri);
        var trimmedBase = ServerUri.AbsoluteUri.TrimEnd('/');
        var trimmedRel = relativeUrl.StartsWith('/') ? relativeUrl : '/' + relativeUrl;

        // Overview opens long-polling requests; about:blank clears them
        // before the next same-origin navigation.
        if (Page.Url.StartsWith(trimmedBase, StringComparison.Ordinal))
        {
            await Page.GotoAsync("about:blank",
                new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 5_000 });
        }

        await Page.GotoAsync(trimmedBase + trimmedRel,
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
    }

    /// <summary>Registers a new user via BTCPay's /register page. Mirrors BTCPay.Tests.PlaywrightTester.RegisterNewUser.</summary>
    protected async Task<string> RegisterNewUser(bool isAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(Page);

        var email = RandomUtils.GetUInt256().ToString().Substring(64 - 20) + "@a.com";
        await Page.FillAsync("#Email", email);
        await Page.FillAsync("#Password", "Passw0rd!");
        await Page.FillAsync("#ConfirmPassword", "Passw0rd!");
        if (isAdmin)
            await Page.ClickAsync("#IsAdmin");
        await Page.ClickAsync("#RegisterButton");

        CreatedUser = email;
        Password = "Passw0rd!";
        return email;
    }

    /// <summary>Creates a store via /stores/create. Matches BTCPay's #Create input + #Name field.</summary>
    protected async Task<string> CreateStore(string? name = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl("/stores/create");
        name ??= "ArkadeStore" + RandomUtils.GetUInt64();
        await Page.FillAsync("#Name", name);
        await Page.ClickAsync("#Create");
        // The General settings page exposes the generated store id in #Id.
        await Page.ClickAsync("#menu-item-General");
        return await Page.InputValueAsync("#Id");
    }

    protected static Task ConfigureBtcOnchainWalletAsync(ServerTester serverTester, string storeId) =>
        ConfigureBtcOnchainWalletAsync(serverTester.PayTester.ServiceProvider, storeId);

    protected static async Task ConfigureBtcOnchainWalletAsync(IServiceProvider services, string storeId)
    {
        var storeRepository = services.GetRequiredService<StoreRepository>();
        var handlers = services.GetRequiredService<PaymentMethodHandlerDictionary>();
        var networkProvider = services.GetRequiredService<BTCPayNetworkProvider>();
        var walletProvider = services.GetRequiredService<BTCPayWalletProvider>();

        var network = networkProvider.GetNetwork<BTCPayNetwork>("BTC")!;
        var xpub = new ExtKey().Neuter().GetWif(network.NBitcoinNetwork);
        var derivation = DerivationSchemeSettings.Parse(xpub.ToString(), network);
        var wallet = walletProvider.GetWallet("BTC") ??
            throw new InvalidOperationException("BTC wallet is not available.");
        await wallet.TrackAsync(derivation.AccountDerivation);

        var store = await storeRepository.FindStore(storeId) ??
            throw new InvalidOperationException($"Store {storeId} was not found.");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        store.SetPaymentMethodConfig(handlers[paymentMethodId], derivation);
        var storeBlob = store.GetStoreBlob();
        storeBlob.SetExcluded(paymentMethodId, false);
        store.SetStoreBlob(storeBlob);
        await storeRepository.UpdateStore(store);
    }

    /// <summary>Creates a store and completes Arkade initial setup.</summary>
    protected async Task<string> CreateStoreWithArkWalletAsync(string? walletInput = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        var storeId = await CreateStore();
        await GoToUrl($"/plugins/ark/stores/{storeId}/getting-started");
        await Page.ClickAsync("[data-testid='getting-started-continue-btn']");
        await Page.WaitForURLAsync(
            url => url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 30_000 });

        if (walletInput is null)
        {
            await SubmitCreateWalletSetupAsync();
        }
        else
        {
            await SubmitImportWalletSetupAsync(walletInput);
        }

        // Generous timeout because the first wallet creation in a session
        // involves arkd signer registration + a contract derive on a cold
        // gRPC connection (~20-30s on a fresh BTCPay process).
        await Page.WaitForURLAsync(
            url => !url.Contains("/initial-setup"),
            new PageWaitForURLOptions { Timeout = 60_000 });

        // Avoid waiting for overview long-polling requests to go idle.
        await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        return storeId;
    }

    protected async Task OpenCreateWalletSettlementStepAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);

        await Page.ClickAsync("[data-testid='hd-wallet-option']");
        await Page.WaitForSelectorAsync("#createNew [data-settlement-step]:not(.d-none)");
    }

    protected async Task SubmitCreateWalletSetupAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);

        await OpenCreateWalletSettlementStepAsync();
        await Page.ClickAsync("#createNew [data-testid='create-wallet-btn']");
    }

    protected async Task OpenImportWalletSettlementStepAsync(string walletInput)
    {
        ArgumentNullException.ThrowIfNull(Page);

        await Page.ClickAsync("[data-testid='import-wallet-option']");
        await Page.WaitForSelectorAsync("#importExisting.show [data-testid='wallet-import-input']");
        await Page.FillAsync("#importExisting [data-testid='wallet-import-input']", walletInput);
        await Page.ClickAsync("#importExisting [data-testid='import-wallet-next-btn']");
        await Page.WaitForSelectorAsync("#importExisting [data-settlement-step]:not(.d-none)");
    }

    protected async Task SubmitImportWalletSetupAsync(string walletInput)
    {
        ArgumentNullException.ThrowIfNull(Page);

        await OpenImportWalletSettlementStepAsync(walletInput);
        await Page.ClickAsync("#importExisting [data-testid='import-wallet-btn']");
    }

    private static string? _resolvedArkdContainer;

    /// <summary>Returns the arkd container used by the active regtest stack.</summary>
    protected static async Task<string> ResolveArkdContainerAsync()
    {
        if (_resolvedArkdContainer is not null) return _resolvedArkdContainer;
        foreach (var candidate in new[] { "arkd", "ark" })
        {
            var probe = await CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(
                CliWrap.Cli.Wrap("docker")
                    .WithArguments(new[] { "inspect", "-f", "{{.Name}}", candidate })
                    .WithValidation(CliWrap.CommandResultValidation.None));
            if (probe.IsSuccess)
                return _resolvedArkdContainer = candidate;
        }
        throw new InvalidOperationException(
            "Could not find an arkd container (tried 'arkd' and 'ark'). Is the regtest stack up?");
    }

    /// <summary>Mints a credit note via the arkd admin CLI.</summary>
    protected static async Task<string> CreateArkNoteAsync(long amountSats)
    {
        var container = await ResolveArkdContainerAsync();
        var result = await CliWrap.Buffered.BufferedCommandExtensions.ExecuteBufferedAsync(
            CliWrap.Cli.Wrap("docker")
                .WithArguments(new[]
                {
                    "exec", container, "arkd", "note", "--amount", amountSats.ToString()
                })
                .WithValidation(CliWrap.CommandResultValidation.None));

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"arkd note --amount {amountSats} (container '{container}') failed " +
                $"(exit={result.ExitCode}): stdout={result.StandardOutput.Trim()}, " +
                $"stderr={result.StandardError.Trim()}");
        return result.StandardOutput.Trim();
    }

    /// <summary>Initializes and funds the ark CLI for direct out-of-round test payments.</summary>
    protected async Task EnsureArkdCliReadyAsync()
    {
        if (_arkdCliReady) return;
        await _arkdCliSetupLock.WaitAsync();
        try
        {
            if (_arkdCliReady) return;

            var container = await ResolveArkdContainerAsync();

            var configProbe = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", container, "ark", "config" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            var needsInit =
                !configProbe.IsSuccess ||
                configProbe.StandardError.Contains("not initialized", StringComparison.OrdinalIgnoreCase);
            if (needsInit)
            {
                var explorerUrl = Environment.GetEnvironmentVariable("ARKADE_E2E_ARK_EXPLORER") ??
                                  "http://electrs-bitcoin:3002";
                var initResult = await Cli.Wrap("docker")
                    .WithArguments(new[]
                    {
                        "exec", container, "ark", "init",
                        "--server-url", "http://localhost:7070",
                        "--explorer", explorerUrl,
                        "--password", "secret"
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (!initResult.IsSuccess)
                    throw new InvalidOperationException(
                        $"ark init failed (exit={initResult.ExitCode}): " +
                        $"stderr={initResult.StandardError.Trim()}, " +
                        $"stdout={initResult.StandardOutput.Trim()}");
            }

            if (await GetArkdOffchainSatsAsync(container) >= 10_000)
            {
                _arkdCliReady = true;
                return;
            }

            await BoardAndSettleArkCliAsync(container);

            if (await GetArkdOffchainSatsAsync(container) < 10_000)
                throw new InvalidOperationException(
                    "ark settle reported success but off-chain balance still under threshold; " +
                    "arkd may have delayed the commitment tx.");

            _arkdCliReady = true;
        }
        finally
        {
            _arkdCliSetupLock.Release();
        }
    }

    /// <summary>
    /// Funds the in-container ark CLI wallet: faucets 1 BTC to a boarding
    /// address, confirms it, and settles it into off-chain balance.
    /// </summary>
    private static async Task BoardAndSettleArkCliAsync(string container)
    {
        var receiveResult = await Cli.Wrap("docker")
            .WithArguments(new[] { "exec", container, "ark", "receive" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!receiveResult.IsSuccess)
            throw new InvalidOperationException(
                $"ark receive failed: {receiveResult.StandardError.Trim()}");
        using var receiveDoc = JsonDocument.Parse(receiveResult.StandardOutput);
        var boardingAddr = receiveDoc.RootElement.GetProperty("boarding_address").GetString()
            ?? throw new InvalidOperationException("ark receive returned no boarding_address");

        var faucetResult = await Cli.Wrap("docker")
            .WithArguments(["exec", DockerHelper.BitcoinContainer, .. DockerHelper.BitcoinCliArgs, "sendtoaddress", boardingAddr, "1"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!faucetResult.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli sendtoaddress {boardingAddr} failed: {faucetResult.StandardError.Trim()}");

        var mineResult = await Cli.Wrap("docker")
            .WithArguments(["exec", DockerHelper.BitcoinContainer, .. DockerHelper.BitcoinCliArgs, "-generate", "6"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!mineResult.IsSuccess)
            throw new InvalidOperationException(
                $"bitcoin-cli -generate 6 failed: {mineResult.StandardError.Trim()}");

        const int settleAttempts = 5;
        for (var attempt = 1; attempt <= settleAttempts; attempt++)
        {
            var settleResult = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", container, "ark", "settle", "--password", "secret" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (settleResult.IsSuccess) break;

            var fundingStillSettling = settleResult.StandardOutput.Contains(
                "fees (0) exceed total amount (0)", StringComparison.Ordinal);
            if (!fundingStillSettling || attempt == settleAttempts)
                throw new InvalidOperationException(
                    $"ark settle failed (exit={settleResult.ExitCode}, attempt={attempt}/{settleAttempts}): " +
                    $"stderr={settleResult.StandardError.Trim()}, " +
                    $"stdout={settleResult.StandardOutput.Trim()}");

            // Genuine settling window (one-time suite setup, usually skipped):
            // arkd has no observable "funding indexed" signal, only the settle retry.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Cli.Wrap("docker")
                .WithArguments(["exec", DockerHelper.BitcoinContainer, .. DockerHelper.BitcoinCliArgs, "-generate", "1"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
        }
    }

    /// <summary>
    /// Pays an ark address out-of-round from the arkd container's CLI wallet,
    /// re-funding the wallet via boarding + settle when it runs dry.
    /// </summary>
    protected static async Task<string?> ArkSendAsync(string destination, long amountSats)
    {
        var container = await ResolveArkdContainerAsync();
        const int sendAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var result = await Cli.Wrap("docker")
                .WithArguments(new[]
                {
                    "exec", container, "ark", "send",
                    "--to", destination,
                    "--amount", amountSats.ToString(CultureInfo.InvariantCulture),
                    "--password", "secret"
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (result.IsSuccess)
            {
                using var doc = JsonDocument.Parse(result.StandardOutput);
                return doc.RootElement.TryGetProperty("txid", out var txid) ? txid.GetString() : null;
            }

            var output = result.StandardError + result.StandardOutput;
            if (attempt >= sendAttempts)
                throw new InvalidOperationException(
                    $"ark send failed (exit={result.ExitCode}, attempt={attempt}/{sendAttempts}): " +
                    $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}");

            if (output.Contains("not enough funds", StringComparison.OrdinalIgnoreCase))
            {
                await BoardAndSettleArkCliAsync(container);
            }
            else if (output.Contains("VTXO_RECOVERABLE", StringComparison.Ordinal))
            {
                var settleResult = await Cli.Wrap("docker")
                    .WithArguments(new[] { "exec", container, "ark", "settle", "--password", "secret" })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (!settleResult.IsSuccess)
                    throw new InvalidOperationException(
                        $"ark settle after VTXO_RECOVERABLE failed: {settleResult.StandardError.Trim()}");
            }
            else
            {
                throw new InvalidOperationException(
                    $"ark send failed (exit={result.ExitCode}): " +
                    $"{result.StandardError.Trim()} {result.StandardOutput.Trim()}");
            }
        }
    }

    private static async Task<long> GetArkdOffchainSatsAsync(string container)
    {
        var balResult = await Cli.Wrap("docker")
            .WithArguments(new[] { "exec", container, "ark", "balance" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (!balResult.IsSuccess) return 0;

        try
        {
            using var doc = JsonDocument.Parse(balResult.StandardOutput);
            if (doc.RootElement.TryGetProperty("offchain_balance", out var off) &&
                off.TryGetProperty("total", out var total) &&
                total.TryGetInt64(out var sats))
                return sats;
        }
        catch (JsonException)
        {
            return 0;
        }
        return 0;
    }

    /// <summary>Reads the current page's ASP.NET antiforgery token.</summary>
    protected async Task<string?> GetAntiforgeryTokenAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);
        var locator = Page.Locator("input[name='__RequestVerificationToken']").First;
        if (await locator.CountAsync() == 0) return null;
        return await locator.GetAttributeAsync("value");
    }

    /// <summary>
    /// POSTs a form to the store's <c>/plugins/ark/stores/{storeId}/{action}</c> endpoint,
    /// using the overview page's antiforgery token.
    /// </summary>
    protected async Task<IAPIResponse> PostPluginFormAsync(string storeId, string action, string formData = "")
    {
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        return await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/{action}").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["RequestVerificationToken"] = token,
                    ["Content-Type"] = "application/x-www-form-urlencoded"
                },
                Data = formData
            });
    }

    /// <summary>Reads the store's Greenfield arkade balance.</summary>
    protected async Task<(long AvailableSats, long BoardingSats)> GetArkadeBalanceAsync(string storeId)
    {
        var authHeader = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}"))}";
        var resp = await Page!.Context.APIRequest.GetAsync(
            new Uri(ServerUri!, $"/api/v1/stores/{storeId}/arkade/balance").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = authHeader }
            });
        Assert.True(resp.Ok, $"balance returned {resp.Status}");
        using var doc = JsonDocument.Parse(await resp.TextAsync());
        return (doc.RootElement.GetProperty("availableSats").GetInt64(),
                doc.RootElement.GetProperty("boardingSats").GetInt64());
    }

    protected async Task<IAPIResponse> SendGreenfieldAsync(string method, string path, object? body = null)
    {
        var options = new APIRequestContextOptions
        {
            Method = method,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] =
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CreatedUser}:{Password}"))}",
                ["Content-Type"] = "application/json",
            },
            DataObject = body,
        };
        return await Page!.Context.APIRequest.FetchAsync(new Uri(ServerUri!, path).AbsoluteUri, options);
    }

    /// <summary>
    /// Default wait between poll attempts. The conditions polled by these tests hit
    /// local HTTP/DB endpoints where an attempt costs milliseconds, so a tight
    /// interval buys latency without meaningful load. Timeout CEILINGS stay generous —
    /// they only cost time on failure.
    /// </summary>
    protected static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Polls <paramref name="condition"/> until it returns true, failing the test at the deadline.</summary>
    protected static Task PollUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, string failMessage, TimeSpan? interval = null)
        => PollUntilAsync(condition, timeout, () => failMessage, interval);

    protected static async Task PollUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, Func<string> failMessage, TimeSpan? interval = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail(failMessage());
            await Task.Delay(interval ?? DefaultPollInterval);
        }
    }

    /// <summary>Polls Greenfield until the invoice reaches Settled.</summary>
    protected static async Task WaitForInvoiceSettledAsync(
        BTCPayServerClient client, string storeId, string invoiceId, TimeSpan timeout)
    {
        InvoiceStatus? last = null;
        await PollUntilAsync(async () =>
        {
            last = (await client.GetInvoice(storeId, invoiceId)).Status;
            return last == InvoiceStatus.Settled;
        }, timeout, () => $"invoice {invoiceId} never settled (last status: {last})");
    }

    protected async Task<string> PayArkadeInvoiceAsync(
        BTCPayServerClient client,
        string storeId,
        long amountSats)
    {
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amountSats,
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = ["ARKADE"]
            }
        });
        Assert.False(string.IsNullOrEmpty(invoice.Id));

        var destination = await GetArkadeInvoiceDestinationAsync(client, storeId, invoice.Id);
        await EnsureArkdCliReadyAsync();
        await ArkSendAsync(destination, amountSats);

        return invoice.Id;
    }

    /// <summary>The ark address of an invoice's ARKADE payment prompt.</summary>
    protected static async Task<string> GetArkadeInvoiceDestinationAsync(
        BTCPayServerClient client,
        string storeId,
        string invoiceId)
    {
        var methods = await client.GetInvoicePaymentMethods(storeId, invoiceId);
        var arkade = methods.FirstOrDefault(m => m.PaymentMethodId == "ARKADE")
            ?? throw new InvalidOperationException(
                $"invoice {invoiceId} has no ARKADE payment method " +
                $"(got: {string.Join(", ", methods.Select(m => m.PaymentMethodId))})");
        if (string.IsNullOrEmpty(arkade.Destination))
            throw new InvalidOperationException($"invoice {invoiceId} ARKADE prompt has no destination");
        return arkade.Destination;
    }

    /// <summary>
    /// Runs the Payments report for the store through the plugin's provider, asserting on the
    /// way that it replaced BTCPay's default provider rather than being registered alongside it.
    /// </summary>
    protected async Task<(List<string> Fields, IList<IList<object?>> Rows)> QueryPaymentsReportAsync(
        string storeId)
    {
        var provider = Assert.Single(
            Services!.GetServices<BTCPayServer.Services.Reporting.ReportProvider>(),
            p => p.Name == "Payments");
        // Compare by name: BTCPay loads the plugin assembly in its own context, so the
        // provider's CLR type is distinct from the test project's compile-time reference.
        Assert.Equal(
            typeof(BTCPayServer.Plugins.Boltz.Arkade.Services.ArkadePaymentsReportProvider).FullName,
            provider.GetType().FullName);

        var queryContext = new BTCPayServer.Services.Reporting.QueryContext(
            storeId, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await provider.Query(queryContext, CancellationToken.None);
        return (queryContext.ViewDefinition!.Fields.Select(f => f.Name).ToList(), queryContext.Data);
    }

    protected static async Task<string> GetNewRegtestBitcoinAddressAsync()
    {
        var address = (await DockerHelper.Exec(DockerHelper.BitcoinContainer, [.. DockerHelper.BitcoinCliArgs, "getnewaddress"])).Trim();
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("bitcoin-cli getnewaddress returned empty output.");
        return address;
    }

    protected static async Task<ArkSwap> WaitForChainSwapAsync(
        IServiceProvider services,
        string walletId,
        TimeSpan? timeout = null)
        => await WaitForSwapAsync(services, walletId, ArkSwapType.ChainArkToBtc,
            wantedStatuses: null, timeout ?? TimeSpan.FromMinutes(1));

    /// <summary>
    /// Polls swap storage until a swap of <paramref name="type"/> for the wallet reaches one
    /// of <paramref name="wantedStatuses"/> (any status when null), failing fast when it lands
    /// on an unwanted terminal status. <paramref name="mineWhileWaiting"/> nudges the regtest
    /// chain along for swaps that wait on a lockup confirmation.
    /// </summary>
    protected static async Task<ArkSwap> WaitForSwapAsync(
        IServiceProvider services,
        string walletId,
        ArkSwapType type,
        ArkSwapStatus[]? wantedStatuses,
        TimeSpan timeout,
        bool mineWhileWaiting = false)
    {
        var swapStorage = services.GetRequiredService<ISwapStorage>();
        var terminal = new[] { ArkSwapStatus.Settled, ArkSwapStatus.Failed, ArkSwapStatus.Refunded };
        var deadline = DateTimeOffset.UtcNow + timeout;
        var nextMineAt = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var swaps = await swapStorage.GetSwaps(
                walletIds: [walletId],
                swapTypes: [type]);
            var candidates = swaps.Where(s => s.ExpectedAmount > 0).ToArray();

            if (wantedStatuses is null)
            {
                if (candidates.Length > 0)
                    return candidates[0];
            }
            else
            {
                var hit = candidates.FirstOrDefault(s => wantedStatuses.Contains(s.Status));
                if (hit is not null)
                    return hit;

                var unexpectedTerminal = candidates.FirstOrDefault(s =>
                    terminal.Contains(s.Status) && !wantedStatuses.Contains(s.Status));
                if (unexpectedTerminal is not null)
                    throw new InvalidOperationException(
                        $"Swap {unexpectedTerminal.SwapId} reached {unexpectedTerminal.Status} " +
                        $"(wanted {string.Join("/", wantedStatuses)}): {unexpectedTerminal.FailReason}");
            }

            // Poll the storage fast, but keep the block cadence at the previous
            // 1-block-per-2s rhythm — the chain only needs an occasional nudge.
            if (mineWhileWaiting && DateTimeOffset.UtcNow >= nextMineAt)
            {
                await DockerHelper.MineBlocks(1);
                nextMineAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            }

            await Task.Delay(DefaultPollInterval);
        }

        throw new TimeoutException(
            $"No {type} swap{(wantedStatuses is null ? "" : $" with status {string.Join("/", wantedStatuses)}")} " +
            $"was recorded for wallet {walletId} within {timeout}.");
    }

    /// <summary>Generates a receive address directly from the in-process test host.</summary>
    protected static async Task<string> GetStoreReceiveAddressAsync(ServerTester serverTester, string storeId)
    {
        var services = serverTester.PayTester.ServiceProvider;
        var storeRepository = services.GetRequiredService<StoreRepository>();
        var contractService = services.GetRequiredService<IContractService>();
        var clientTransport = services.GetRequiredService<IClientTransport>();

        var paymentMethodId = new PaymentMethodId("ARKADE");
        string? walletId = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var store = await storeRepository.FindStore(storeId) ??
                throw new InvalidOperationException($"Store {storeId} was not found.");
            // The plugin is loaded in an isolated AssemblyLoadContext in E2E,
            // so avoid typed payment config casts across that boundary.
            var config = store.GetPaymentMethodConfig(paymentMethodId);
            walletId = config is null ? null : ReadString(config, "walletId");
            if (!string.IsNullOrWhiteSpace(walletId))
                break;

            await Task.Delay(250);
        }

        if (string.IsNullOrWhiteSpace(walletId))
            throw new InvalidOperationException($"Store {storeId} has no Arkade wallet configured.");

        var terms = await clientTransport.GetServerInfoAsync();
        var contract = await contractService.DeriveContract(
            walletId,
            NextContractPurpose.Receive,
            ContractActivityState.AwaitingFundsBeforeDeactivate,
            metadata: new Dictionary<string, string> { ["Source"] = "manual" });
        var address = contract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);

        Assert.False(string.IsNullOrWhiteSpace(address), $"Store {storeId} has no receive address");
        return address;
    }

    protected static string? ReadString(JToken token, string camelCaseName) =>
        token.Value<string>(camelCaseName) ??
        token.Value<string>(char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..]);

    /// <summary>Reads the store's configured Arkade wallet id from overview.</summary>
    protected async Task<string?> GetStoreWalletIdAsync(string storeId)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        return await Page.GetAttributeAsync(".truncate-center-id", "data-text");
    }

    /// <summary>Mints an arkd note and imports it into the plugin wallet.</summary>
    protected async Task FundWalletViaNoteAsync(
        IServiceProvider serviceProvider, string walletId, long amountSats)
    {
        var note = await CreateArkNoteAsync(amountSats);
        if (string.IsNullOrEmpty(note))
            throw new InvalidOperationException("arkd note CLI returned empty");
        var contractService = serviceProvider.GetRequiredService<IContractService>();
        await contractService.ImportContract(walletId, ArkNoteContract.Parse(note));
    }

    protected Task FundWalletViaNoteAsync(
        ServerTester serverTester, string walletId, long amountSats) =>
        FundWalletViaNoteAsync(serverTester.PayTester.ServiceProvider, walletId, amountSats);

    protected async Task<string> FundStoreWalletViaNoteAsync(
        ServerTester serverTester, string storeId, long amountSats)
    {
        var walletId = await GetStoreWalletIdAsync(storeId);
        if (string.IsNullOrWhiteSpace(walletId))
            throw new InvalidOperationException($"Store {storeId} has no wallet id.");
        await FundWalletViaNoteAsync(serverTester, walletId, amountSats);
        return walletId;
    }

    /// <summary>
    /// Reloads a plugin page until a selector is rendered and visible.
    /// Use this for UI states driven by async wallet/indexer work where a
    /// balance threshold would couple the test to unrelated fee mechanics.
    /// </summary>
    protected async Task WaitForVisibleSelectorAsync(
        string relativeUrl, string selector, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(Page);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await GoToUrl(relativeUrl);
            var locator = Page.Locator(selector).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                return;
            // Each attempt already costs a full page load, which self-throttles on
            // slower runners; the extra delay only needs to yield, not pace.
            await Task.Delay(DefaultPollInterval);
        }
        throw new TimeoutException($"Selector {selector} was not visible on {relativeUrl}.");
    }

    /// <summary>
    /// Polls /suggest-coins until it returns outpoints that are also free of
    /// locally active intents (the real precondition for spend/payout/estimate),
    /// not just a rendered balance.
    /// The Arkade overview shows an inbound note VTXO the instant arkd's
    /// indexer reports it, but it isn't spendable until the redemption batch
    /// settles — waiting on the displayed balance races that gap and yields
    /// "No spendable coins" the moment you try to use it. Returns the
    /// outpoints once available.
    /// </summary>
    protected async Task<List<string>> PollForSpendableCoinsAsync(
        string storeId, string destinationType, long amountSats, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        string? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var outpoints = await SuggestOutpointsAsync(storeId, destinationType, amountSats);
                if (outpoints.Count > 0 && await AreOutpointsFreeOfActiveIntents(storeId, outpoints))
                    return outpoints;
            }
            // Freshly redeemed note VTXOs can be absent or recoverable until
            // the next batch settles.
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("No spendable coins") ||
                ex.Message.Contains("non-recoverable coins"))
            {
                lastError = ex.Message;
            }
            await Task.Delay(DefaultPollInterval);
        }
        throw new TimeoutException(
            $"Store {storeId} had no spendable coins within the wait window (last: {lastError ?? "empty selection"}).");
    }

    private async Task<bool> AreOutpointsFreeOfActiveIntents(string storeId, IReadOnlyCollection<string> outpoints)
    {
        if (Services is null)
            return true;

        var walletId = await GetStoreWalletIdAsync(storeId);
        if (string.IsNullOrWhiteSpace(walletId))
            return false;

        // GetLockedVtxoOutpoints currently omits BatchInProgress, although arkd
        // still rejects those inputs as VTXO_ALREADY_REGISTERED.
        var intents = await Services.GetRequiredService<IIntentStorage>().GetIntents(
            walletIds: [walletId],
            states:
            [
                ArkIntentState.WaitingToSubmit,
                ArkIntentState.WaitingForBatch,
                ArkIntentState.BatchInProgress
            ]);
        var lockedOutpoints = intents
            .SelectMany(intent => intent.IntentVtxos)
            .Select(outpoint => $"{outpoint.Hash}:{outpoint.N}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return !outpoints.Any(lockedOutpoints.Contains);
    }

    /// <summary>Calls POST /suggest-coins and returns selected outpoints.</summary>
    protected async Task<List<string>> SuggestOutpointsAsync(
        string storeId, string destinationType, long amountSats)
    {
        ArgumentNullException.ThrowIfNull(Page);
        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var resp = await Page.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/plugins/ark/stores/{storeId}/suggest-coins").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["RequestVerificationToken"] = token
                },
                DataObject = new { destinationType, amountSats }
            });

        var raw = await resp.TextAsync();
        if (!resp.Ok)
            throw new InvalidOperationException($"suggest-coins returned {resp.Status}: {raw}");
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.GetString() is { Length: > 0 } msg)
            throw new InvalidOperationException($"suggest-coins error: {msg}");
        if (!root.TryGetProperty("suggestedOutpoints", out var op) ||
            op.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];
        return op.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
    }

    public void Dispose()
    {
        Try(() => { Page?.CloseAsync().GetAwaiter().GetResult(); Page = null; });
        Try(() => { Browser?.CloseAsync().GetAwaiter().GetResult(); Browser = null; });
        Try(() => { Playwright?.Dispose(); Playwright = null; });
        Services = null;
        GC.SuppressFinalize(this);

        static void Try(Action a)
        {
            try { a(); } catch { /* test teardown: don't mask the real failure */ }
        }
    }
}
