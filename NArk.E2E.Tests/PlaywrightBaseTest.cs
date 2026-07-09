using BTCPayServer;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NArk.Abstractions.Contracts;
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

    private static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static readonly SemaphoreSlim _arkdCliSetupLock = new(1, 1);
    private static bool _arkdCliReady;

    /// <summary>Starts a Chromium browser and opens a page pointed at the running BTCPay.</summary>
    protected async Task InitializePlaywright(ServerTester serverTester)
    {
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
            "Could not find an arkd container (tried 'arkd' and 'ark'). Is the nigiri stack up?");
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

    /// <summary>Initializes and funds the ark CLI for checkout cheat-mode sends.</summary>
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
                var explorerUrl = Environment.GetEnvironmentVariable("ARKADE_CHEAT_ARK_EXPLORER") ??
                                  "http://mempool_web/api";
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
                .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "sendtoaddress", boardingAddr, "1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            if (!faucetResult.IsSuccess)
                throw new InvalidOperationException(
                    $"bitcoin-cli sendtoaddress {boardingAddr} failed: {faucetResult.StandardError.Trim()}");

            var mineResult = await Cli.Wrap("docker")
                .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "-generate", "6" })
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

                await Task.Delay(TimeSpan.FromSeconds(2));
                await Cli.Wrap("docker")
                    .WithArguments(new[] { "exec", "bitcoin", "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123", "-generate", "1" })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }

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

    protected async Task PayArkadeInvoiceAsync(
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

        await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
        var token = (await GetAntiforgeryTokenAsync()) ?? "";
        var payResp = await Page!.Context.APIRequest.PostAsync(
            new Uri(ServerUri!, $"/i/{invoice.Id}/test-payment").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                    ["RequestVerificationToken"] = token
                },
                Data = $"Amount={amountSats}&CryptoCode=SATS&PaymentMethodId=ARKADE"
            });

        var payBody = await payResp.TextAsync();
        Assert.True(payResp.Ok,
            $"POST /i/{invoice.Id}/test-payment returned {payResp.Status}: {payBody}");
    }

    protected static async Task<string> GetNewRegtestBitcoinAddressAsync()
    {
        var address = (await DockerHelper.Exec("bitcoin",
            [
                "bitcoin-cli", "-regtest", "-rpcuser=admin1", "-rpcpassword=123",
                "getnewaddress"
            ])).Trim();
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException("bitcoin-cli getnewaddress returned empty output.");
        return address;
    }

    protected static async Task<ArkSwap> WaitForChainSwapAsync(
        IServiceProvider services,
        string walletId,
        TimeSpan? timeout = null)
    {
        var swapStorage = services.GetRequiredService<ISwapStorage>();
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(1));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var swap = (await swapStorage.GetSwaps(
                    walletIds: [walletId],
                    swapTypes: [ArkSwapType.ChainArkToBtc]))
                .FirstOrDefault(s => s.ExpectedAmount > 0);
            if (swap is not null)
                return swap;

            await Task.Delay(2_000);
        }

        throw new TimeoutException($"No ChainArkToBtc swap was recorded for wallet {walletId}.");
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
    /// Reads [data-testid='wallet-balance'] from the current overview
    /// page. The plugin renders available balance as a BTC-denominated
    /// string (DisplayFormatter.Currency); the first decimal in the text
    /// is parsed and ×1e8 to sats.
    /// </summary>
    protected async Task<long> ReadAvailableBalanceSatsAsync()
    {
        ArgumentNullException.ThrowIfNull(Page);
        var locator = Page.Locator("[data-testid='wallet-balance']").First;
        if (await locator.CountAsync() == 0) return 0;
        var text = await locator.InnerTextAsync();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:[.,]\d+)?");
        if (!match.Success) return 0;
        if (!decimal.TryParse(
                match.Value.Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var btc)) return 0;
        return (long)(btc * 100_000_000m);
    }

    /// <summary>
    /// Polls the overview balance until it reaches <paramref name="minSats"/>
    /// or the timeout elapses. Returns the last observed balance on success.
    /// </summary>
    protected async Task<long> PollForBalanceAsync(
        string storeId, long minSats, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        long last = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await GoToUrl($"/plugins/ark/stores/{storeId}/overview");
            last = await ReadAvailableBalanceSatsAsync();
            if (last >= minSats) return last;
            await Task.Delay(3_000);
        }
        throw new TimeoutException(
            $"Wallet {storeId} balance never reached {minSats} sats (last: {last}).");
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
            await Task.Delay(3_000);
        }
        throw new TimeoutException($"Selector {selector} was not visible on {relativeUrl}.");
    }

    /// <summary>
    /// Polls /suggest-coins until it returns spendable outpoints (the real
    /// precondition for spend/payout/estimate), not just a rendered balance.
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
                if (outpoints.Count > 0) return outpoints;
            }
            // Freshly redeemed note VTXOs can be absent or recoverable until
            // the next batch settles.
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("No spendable coins") ||
                ex.Message.Contains("non-recoverable coins"))
            {
                lastError = ex.Message;
            }
            await Task.Delay(3_000);
        }
        throw new TimeoutException(
            $"Store {storeId} had no spendable coins within the wait window (last: {lastError ?? "empty selection"}).");
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
        GC.SuppressFinalize(this);

        static void Try(Action a)
        {
            try { a(); } catch { /* test teardown: don't mask the real failure */ }
        }
    }
}
