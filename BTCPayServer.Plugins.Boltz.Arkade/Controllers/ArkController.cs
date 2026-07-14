using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.Boltz.Arkade.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Exceptions;
using BTCPayServer.Plugins.Boltz.Arkade.Helpers;
using BTCPayServer.Plugins.Boltz.Arkade.Lightning;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.Models.Api;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark;
using BTCPayServer.Plugins.Boltz.Arkade.Services;
using BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;
using BTCPayServer.Plugins.Boltz.Arkade.Services.WalletLogger;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Chain;
using NArk.Core.Contracts;
using NArk.Hosting;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;
using NArk.Storage.EfCore.Entities;
using NArk.Core.Wallet;
using LNURL;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using Newtonsoft.Json.Linq;
namespace BTCPayServer.Plugins.Boltz.Arkade.Controllers;

[Route("plugins/ark")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ArkController(
    BoltzLimitsValidator? boltzLimitsValidator,
    BoltzClient? boltzClient,
    ArkNetworkConfig arkNetworkConfig,
    BTCPayNetworkProvider networkProvider,
    ArkPayoutFulfillmentService payoutFulfillment,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IClientTransport clientTransport,
    ArkOperatorHealthService arkOperatorHealth,
    BoltzHealthService boltzHealth,
    ArkadeSpendingService arkadeSpendingService,
    ArkadeWalletService walletService,
    ArkWalletOwnershipService walletOwnership,
    ArkAutomatedPayoutSenderFactory payoutSenderFactory,
    PayoutProcessorService payoutProcessorService,
    IEnumerable<ISettlementOption> settlementOptions,
    InvoiceRepository invoiceRepository,
    EventAggregator eventAggregator,
    IIntentGenerationService intentGenerationService,
    IIntentStorage intentStorage,
    IWalletProvider walletProvider,
    ISpendingService arkadeSpender,
    IFeeEstimator feeEstimator,
    IContractService contractService,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    IWalletStorage walletStorage,
    IDbContextFactory<ArkPluginDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    BoardingUtxoSyncService boardingUtxoSyncService,
    IWalletLogStore walletLogStore,
    RecoveryStatusTracker recoveryStatusTracker,
    IServiceProvider serviceProvider,
    ILogger<ArkController> logger) : Controller
{
    // Post-operation VTXO refresh only needs to catch updates since the operation
    // started. A 5-minute buffer absorbs clock skew and batch-round latency while
    // keeping the arkd indexer query bounded for wallets with lots of history.
    private static readonly TimeSpan PostOpVtxoPollBuffer = TimeSpan.FromMinutes(5);
    private static DateTimeOffset PostOpVtxoPollSince() => DateTimeOffset.UtcNow - PostOpVtxoPollBuffer;

    [HttpGet("stores/{storeId}/getting-started")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult GettingStarted(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return View();

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpGet("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId == null)
        {
            return View(await CreateInitialWalletSetupViewModel(store, cancellationToken: cancellationToken));
        }

        return RedirectToAction(nameof(StoreOverview), new { storeId });
    }

    [HttpPost("stores/{storeId}/initial-setup")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> InitialSetup(string storeId, InitialWalletSetupViewModel model)
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        model.SettlementInputs = ReadSettlementInputsFromForm();
        model = await CreateInitialWalletSetupViewModel(store, model, HttpContext.RequestAborted);
        foreach (var option in settlementOptions)
        {
            var validationResult = await option.ValidateInput(
                store,
                model.SettlementInputs.GetValueOrDefault(option.Type),
                HttpContext.RequestAborted);
            if (validationResult is null)
                continue;

            ModelState.AddModelError(validationResult.FieldName, validationResult.Message);
            return View(model);
        }

        try
        {
            var walletSettings = GetFromInputWallet(model.Wallet);

            string walletId;
            try
            {
                var serverInfo = await clientTransport.GetServerInfoAsync(HttpContext.RequestAborted);
                var wallet = await WalletFactory.CreateWallet(
                    walletSettings.Wallet,
                    destination: null,
                    serverInfo,
                    HttpContext.RequestAborted);

                if (await walletOwnership.IsWalletUsedByAnyStore(wallet.Id, excludeStoreId: store.Id))
                {
                    ModelState.AddModelError(nameof(model.Wallet), "This wallet is already in use by another store.");
                    return View(model);
                }

                await walletStorage.UpsertWallet(wallet, updateIfExists: true, HttpContext.RequestAborted);

                walletId = wallet.Id;
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Could not update wallet");
                return View(model);
            }

            // Background recovery scans derivation paths, restores swaps,
            // finalizes pending txs, and syncs boarding UTXOs.
            StartBackgroundRecovery(walletId);

            var config = new ArkadePaymentMethodConfig(
                walletId,
                WalletBackedUp: !walletSettings.IsNewlyGeneratedWallet);
            foreach (var option in settlementOptions)
                config = option.ApplyInitialSetupDefault(
                    store,
                    config,
                    model.SettlementInputs.GetValueOrDefault(option.Type));
            store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], config);

            // Set Arkade as the default payment method
            store.SetDefaultPaymentId(ArkadePlugin.ArkadePaymentMethodId);

            // Enable Lightning by default if not already configured.
            var lightningPaymentMethodId = GetLightningPaymentMethod();
            var existingLnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(lightningPaymentMethodId, paymentMethodHandlerDictionary);
            if (existingLnConfig == null)
            {
                var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

                var lnConfig = new LightningPaymentMethodConfig()
                {
                    ConnectionString = await walletOwnership.CreateLightningConnectionString(config.WalletId, HttpContext.RequestAborted),
                };

                store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
                store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lnurlPaymentMethodId], new LNURLPaymentMethodConfig
                {
                    UseBech32Scheme = true,
                    LUD12Enabled = false
                });

                var blob = store.GetStoreBlob();
                blob.SetExcluded(lightningPaymentMethodId, false);
                blob.OnChainWithLnInvoiceFallback = true;
                store.SetStoreBlob(blob);
            }

            await storeRepository.UpdateStore(store);

            TempData[WellKnownTempData.SuccessMessage] = "Arkade payment method updated.";

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Arkade initial setup failed for store {StoreId}", store.Id);
            ModelState.AddModelError(nameof(model.Wallet), DescribeArkError(ex, "Could not set up wallet"));
            return View(model);
        }
    }

    /// <summary>
    /// Starts unified wallet recovery for <paramref name="walletId"/> on a background
    /// thread (a gap-limit scan polls arkd per index), tracking status for the overview.
    /// Discovers contracts and the derivation index, restores swaps, finalizes
    /// pending txs and resyncs offchain funds, then
    /// syncs boarding (on-chain) UTXOs. <c>IWalletRecoveryService</c> is only registered
    /// when swaps (Boltz) are configured; without it this degrades to a boarding-only sync.
    /// </summary>
    private void StartBackgroundRecovery(string walletId)
    {
        var recoveryService = serviceProvider.GetService<NArk.Swaps.Recovery.IWalletRecoveryService>();
        _ = Task.Run(async () =>
        {
            try
            {
                recoveryStatusTracker.SetRunning(walletId);

                var contractsRecovered = 0;
                var swapsAudited = 0;
                var fundsSynced = 0;
                if (recoveryService is not null)
                {
                    var report = await recoveryService.RecoverAsync(walletId, cancellationToken: CancellationToken.None);
                    contractsRecovered = report.ContractsRecovered;
                    swapsAudited = report.SwapAudit.Count;
                    fundsSynced = report.FundsScriptsSynced;
                }

                // Boarding (on-chain) UTXOs aren't covered by offchain recovery.
                var boardingContracts = (await contractStorage.GetContracts(
                        walletIds: [walletId], scope: ContractScope.Onchain,
                        cancellationToken: CancellationToken.None)).ToList();
                if (boardingContracts.Count > 0)
                    await boardingUtxoSyncService.SyncAsync(boardingContracts, CancellationToken.None);

                recoveryStatusTracker.SetCompleted(walletId,
                    recoveryService is not null ? contractsRecovered : boardingContracts.Count,
                    swapsAudited, fundsSynced);
            }
            catch (Exception ex)
            {
                recoveryStatusTracker.SetFailed(walletId, ArkOperatorAvailability.Describe(ex));
                logger.LogWarning(ex, "Background wallet recovery failed for wallet {WalletId}", walletId);
            }
        });
    }

    [HttpPost("stores/{storeId}/rescan")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult Rescan(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        StartBackgroundRecovery(config!.WalletId!);
        return RedirectWithSuccess(nameof(StoreOverview),
            "Wallet rescan started — contracts, funds and swaps will refresh shortly.", new { storeId });
    }

    [HttpGet("stores/{storeId}/overview")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> StoreOverview(CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var wallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);

        // Get balances with error handling - indexer service may be unavailable
        ArkBalancesViewModel? balances = null;
        try
        {
            balances = await walletService.GetBalances(config.WalletId!, cancellationToken);
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Unable to fetch balances");
        }

        var signerAvailable = await walletProvider.GetAddressProviderAsync(config.WalletId!, cancellationToken) != null;

        var arkOperatorStatus = await arkOperatorHealth.GetStatusAsync(cancellationToken);

        // Check Boltz connection and get cached limits
        var (boltzConnected, boltzError, boltzLimits) = await GetBoltzConnectionStatusAsync(cancellationToken);

        var recentPayments = new List<RecentPaymentViewModel>();
        var receivedVolumeSats = 0L;
        var processingPaymentCount = 0;
        var lightningPaymentMethod = GetLightningPaymentMethod();
        var lnurlPaymentMethod = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
        var paymentMethods = new HashSet<PaymentMethodId>
        {
            ArkadePlugin.ArkadePaymentMethodId,
            lightningPaymentMethod,
            lnurlPaymentMethod
        };

        try
        {
            var invoices = await invoiceRepository.GetInvoices(new InvoiceQuery
            {
                StoreId = [store!.Id],
                IncludeArchived = false,
                Take = 25
            }, cancellationToken);

            foreach (var payment in invoices
                         .SelectMany(i => i.GetPayments(false))
                         .Where(p => paymentMethods.Contains(p.PaymentMethodId)))
            {
                if (payment.Status is PaymentStatus.Settled or PaymentStatus.Processing)
                {
                    receivedVolumeSats += Money.Coins(payment.Value).Satoshi;
                }

                if (payment.Status == PaymentStatus.Processing)
                {
                    processingPaymentCount++;
                }

                var isLightningPayment = payment.PaymentMethodId == lightningPaymentMethod ||
                                         payment.PaymentMethodId == lnurlPaymentMethod;

                recentPayments.Add(new RecentPaymentViewModel
                {
                    Date = payment.ReceivedTime,
                    Title = "Payment received",
                    Description = isLightningPayment ? "Lightning" : "Bitcoin",
                    Amount = payment.Value,
                    Currency = payment.Currency,
                    PaymentStatus = payment.Status
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load recent invoice payments for store {StoreId}", store!.Id);
        }

        var totalVtxoCount = 0;
        IReadOnlyCollection<ArkVtxo> walletVtxos = [];
        IReadOnlyCollection<ArkContractEntity> walletContracts = [];
        try
        {
            walletVtxos = await vtxoStorage.GetVtxos(
                walletIds: [config.WalletId!],
                includeSpent: true,
                cancellationToken: cancellationToken);
            walletContracts = await contractStorage.GetContracts(
                walletIds: [config.WalletId!],
                cancellationToken: cancellationToken);
            totalVtxoCount = walletVtxos.Count;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load VTXOs and contracts for wallet {WalletId}", config.WalletId);
        }

        var recentPaymentCount = recentPayments.Count;
        var totalIntentCount = 0;
        var totalSwapCount = 0;
        var pendingLightningSwapCount = 0;
        var swapFeesSats = 0L;
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            totalIntentCount = await db.Intents.CountAsync(i => i.WalletId == config.WalletId!, cancellationToken);
            totalSwapCount = await db.Swaps.CountAsync(s => s.WalletId == config.WalletId!, cancellationToken);

            var lightningSwapTypes = new[] { ArkSwapType.ReverseSubmarine, ArkSwapType.Submarine };
            var lightningSwaps = db.Swaps
                .Where(s => s.WalletId == config.WalletId! && lightningSwapTypes.Contains(s.SwapType));

            pendingLightningSwapCount = await lightningSwaps
                .CountAsync(s => s.Status == ArkSwapStatus.Pending || s.Status == ArkSwapStatus.Unknown, cancellationToken);

            var visibleLightningSwaps = await lightningSwaps
                .Where(s => s.SwapType == ArkSwapType.ReverseSubmarine &&
                            (s.Status == ArkSwapStatus.Pending ||
                             s.Status == ArkSwapStatus.Unknown))
                .OrderByDescending(s => s.UpdatedAt)
                .Take(5)
                .ToListAsync(cancellationToken);
            foreach (var swap in visibleLightningSwaps)
            {
                recentPayments.Add(new RecentPaymentViewModel
                {
                    Date = swap.UpdatedAt,
                    Title = "Receiving payment",
                    Description = "Lightning",
                    Amount = Money.Satoshis(swap.ExpectedAmount).ToDecimal(MoneyUnit.BTC),
                    Currency = "BTC",
                    SwapStatus = swap.Status
                });
            }

            var settlementSwaps = await db.Swaps
                .Where(s => s.WalletId == config.WalletId! && s.SwapType == ArkSwapType.ChainArkToBtc)
                .OrderByDescending(s => s.UpdatedAt)
                .Take(5)
                .ToListAsync(cancellationToken);

            // Fee stat: project only the columns the fee math needs.
            var network = networkProvider.BTC.NBitcoinNetwork;
            var settledSwaps = await db.Swaps
                .Where(s => s.WalletId == config.WalletId! && s.Status == ArkSwapStatus.Settled)
                .Select(s => new { s.SwapType, s.ExpectedAmount, s.Invoice, s.MetadataJson })
                .ToListAsync(cancellationToken);
            swapFeesSats = settledSwaps.Sum(s =>
                GetSettledSwapFeeSats(s.SwapType, s.ExpectedAmount, s.Invoice, s.MetadataJson, network) ?? 0L);

            foreach (var swap in settlementSwaps)
            {
                var (sourceAmountSats, destinationAmountSats, feesPaidSats) =
                    GetChainSwapAmounts(swap.ExpectedAmount, swap.MetadataJson);
                var displayAmountSats = destinationAmountSats ?? sourceAmountSats;

                recentPayments.Add(new RecentPaymentViewModel
                {
                    Date = swap.UpdatedAt,
                    Title = "Mainchain settlement",
                    Description = "Arkade to Bitcoin",
                    Amount = Money.Satoshis(displayAmountSats).ToDecimal(MoneyUnit.BTC),
                    Currency = "BTC",
                    AmountPrefix = "",
                    AmountSubtext = GetSettlementSubtext(swap.Status, feesPaidSats),
                    AmountSubtextSensitive = swap.Status == ArkSwapStatus.Settled && feesPaidSats.HasValue,
                    ShowAmount = swap.Status is not (ArkSwapStatus.Failed or ArkSwapStatus.Refunded),
                    SwapStatus = swap.Status
                });
            }

            var swapContractScripts = await db.Swaps
                .Where(s => s.WalletId == config.WalletId!)
                .Select(s => s.ContractScript)
                .ToListAsync(cancellationToken);

            recentPayments.AddRange(WalletActivityBuilder.BuildEntries(
                walletVtxos, walletContracts, swapContractScripts.ToHashSet()));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load activity counts for wallet {WalletId}", config.WalletId);
        }

        recentPayments = [.. recentPayments.OrderByDescending(p => p.Date).Take(5)];
        var paymentStats = new List<StoreOverviewStatViewModel>
        {
            new() { Name = "Recent volume", Value = receivedVolumeSats, Unit = StoreOverviewStatUnit.Sats },
            new() { Name = "Recent payments", Value = recentPaymentCount },
            new() { Name = "In progress", Value = processingPaymentCount + pendingLightningSwapCount },
            new() { Name = "Swap fees", Value = swapFeesSats, Unit = StoreOverviewStatUnit.Sats }
        };

        return View(new StoreOverviewViewModel
        {
            StoreId = store!.Id,
            IsLightningEnabled = IsArkadeLightningEnabled(),
            Balances = balances,
            WalletId = config.WalletId,
            SignerAvailable = signerAvailable,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            WalletBackedUp = config.WalletBackedUp ?? true,
            HasCurrentWalletFunds = totalVtxoCount > 0,
            HasSecret = !string.IsNullOrEmpty(wallet?.Secret),
            WalletType = wallet?.WalletType ?? WalletType.HD,
            RecoveryStatus = config.WalletId is { } recoveryWalletId ? recoveryStatusTracker.Get(recoveryWalletId) : null,
            ArkOperatorUrl = arkNetworkConfig.ArkUri,
            ArkOperatorConnected = arkOperatorStatus.Available,
            ArkOperatorError = arkOperatorStatus.Error,
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError,
            BoltzReverseMinAmount = boltzLimits?.ReverseMinAmount,
            BoltzReverseMaxAmount = boltzLimits?.ReverseMaxAmount,
            BoltzReverseFeePercentage = boltzLimits?.ReverseFeePercentage,
            BoltzReverseMinerFee = boltzLimits?.ReverseMinerFee,
            BoltzSubmarineMinAmount = boltzLimits?.SubmarineMinAmount,
            BoltzSubmarineMaxAmount = boltzLimits?.SubmarineMaxAmount,
            BoltzSubmarineFeePercentage = boltzLimits?.SubmarineFeePercentage,
            BoltzSubmarineMinerFee = boltzLimits?.SubmarineMinerFee,
            TotalVtxoCount = totalVtxoCount,
            TotalIntentCount = totalIntentCount,
            TotalSwapCount = totalSwapCount,
            PaymentStats = paymentStats,
            RecentPayments = recentPayments
        });
    }

    [HttpGet("stores/{storeId}/wallet-log")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult DownloadWalletLog(string storeId)
    {
        var (_, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var walletId = config!.WalletId!;
        var filename = $"arkade-wallet-{walletId}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.log";
        var stream = walletLogStore.OpenForRead(walletId);
        if (stream is null)
        {
            const string noEntries =
                "No diagnostic log entries have been recorded for this wallet yet.\n";
            return File(System.Text.Encoding.UTF8.GetBytes(noEntries),
                "text/plain; charset=utf-8", filename);
        }

        return File(stream, "text/plain; charset=utf-8", filename);
    }

    [HttpGet("stores/{storeId}/configuration")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult Configuration(string storeId)
    {
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpGet("stores/{storeId}/settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Settings(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var wallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);
        var (boltzConnected, boltzError, _) = await GetBoltzConnectionStatusAsync(cancellationToken);

        return View("Settings", new StoreSettingsViewModel
        {
            WalletType = wallet?.WalletType ?? WalletType.HD,
            IsLightningEnabled = IsArkadeLightningEnabled(),
            Form = new StoreSettingsFormModel(),
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            SettlementOptions = await CreateSettlementOptions(store!, config, cancellationToken: cancellationToken),
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError
        });
    }

    [HttpPost("stores/{storeId}/show-private-key")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ShowPrivateKey(string storeId, string? returnTo = null)
    {
        var (_, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var wallet = await walletStorage.GetWalletById(config!.WalletId);
        if (wallet?.Secret == null)
            return NotFound();

        var returnAction = returnTo == "settings" ? nameof(Settings) : nameof(StoreOverview);
        return this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
        {
            ReturnUrl = Url.Action(returnAction, new { storeId }),
            IsStored = true,
            RequireConfirm = false,
            CryptoCode = "ARK",
            Mnemonic = wallet.Secret
        });
    }

    /// <summary>
    /// Receive page: shows existing manual receive address or prompts to generate one.
    /// </summary>
    [HttpGet("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var model = new ArkReceiveViewModel();

        try
        {
            var existingAddress = await walletService.FindManualReceiveAddress(config!.WalletId!, cancellationToken);
            if (existingAddress != null)
                model.Address = existingAddress;

            var existingBoarding = await walletService.FindManualBoardingAddress(config.WalletId!, cancellationToken);
            if (existingBoarding != null)
                model.BoardingAddress = existingBoarding;
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Failed to check receive address");
        }

        return View(model);
    }

    [HttpPost("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, string command, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var model = new ArkReceiveViewModel();
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

            if (command == "generate-boarding-address")
            {
                var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
                    config!.WalletId!,
                    NextContractPurpose.Boarding,
                    ContractActivityState.AwaitingFundsBeforeDeactivate,
                    metadata: new Dictionary<string, string> { ["Source"] = "manual" },
                    cancellationToken: cancellationToken);
                model.BoardingAddress = boardingContract.GetOnchainAddress(terms.Network).ToString();

                // Preserve existing ark address if any
                var existingAddress = await walletService.FindManualReceiveAddress(config.WalletId!, cancellationToken);
                if (existingAddress != null) model.Address = existingAddress;
            }
            else
            {
                var contract = await contractService.DeriveContract(
                    config!.WalletId!,
                    NextContractPurpose.Receive,
                    ContractActivityState.AwaitingFundsBeforeDeactivate,
                    metadata: new Dictionary<string, string> { ["Source"] = "manual" },
                    cancellationToken: cancellationToken);
                model.Address = contract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);

                // Preserve existing boarding address if any
                var existingBoarding = await walletService.FindManualBoardingAddress(config.WalletId!, cancellationToken);
                if (existingBoarding != null) model.BoardingAddress = existingBoarding;
            }

            return View(model);
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Failed to generate address");
        }

        return RedirectToAction(nameof(Receive), new { storeId });
    }

    [HttpPost("stores/{storeId}/estimate-fees")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EstimateFees(string storeId, [FromBody] FeeEstimateRequest request, CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return BadRequest("Invalid store settings");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var response = new FeeEstimateResponse();

            // Check if this is a Lightning payment
            if (request.Outputs.Count == 1)
            {
                var dest = request.Outputs[0].Destination?.Trim() ?? "";
                if (IsLightningDestination(dest))
                {
                    // Lightning swap fees
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                        if (limits != null)
                        {
                            var amount = request.Outputs[0].AmountSats ?? request.TotalInputSats;

                            response.IsLightning = true;
                            response.FeePercentage = limits.SubmarineFeePercentage * 100; // Convert to percentage for display
                            response.MinerFeeSats = limits.SubmarineMinerFee;
                            response.EstimatedFeeSats = (long)Math.Ceiling(amount * limits.SubmarineFeePercentage) + limits.SubmarineMinerFee;
                            response.FeeDescription = $"{limits.SubmarineFeePercentage * 100:F2}% + {limits.SubmarineMinerFee} sats miner fee";
                        }
                        else
                        {
                            response.Error = "Failed to fetch Boltz limits";
                        }
                    }
                    else
                    {
                        response.Error = "Lightning swaps not available";
                    }

                    return Json(response);
                }

                // Arkade-mode Bitcoin destination: within the chain-swap limits it settles via an
                // Arkade→BTC chain swap (same mechanism as automatic mainchain settlement). When the
                // amount is outside those limits (or chain swaps are unavailable) the /send endpoint
                // silently falls back to a Batch settlement, so mirror that here by falling through
                // to the Batch fee estimate below instead of erroring — keeping the wizard usable.
                if (string.Equals(request.SpendType, "Arkade", StringComparison.OrdinalIgnoreCase))
                {
                    var parseResult = ArkSpendHelpers.ParseOutputDestination(dest, serverInfo.Network);
                    if (parseResult.Destination != null && parseResult.OutputType == ArkTxOutType.Onchain)
                    {
                        // Same source as MainchainSettlementService.GetMainchainSettlementLimits
                        var chainLimits = boltzLimitsValidator != null
                            ? await boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: false, token)
                            : null;
                        if (chainLimits != null)
                        {
                            var amount = request.Outputs[0].AmountSats ?? request.TotalInputSats;
                            if (amount > 0 && amount >= chainLimits.MinAmount && amount <= chainLimits.MaxAmount)
                            {
                                response.IsChainSwap = true;
                                response.FeePercentage = chainLimits.FeePercentage * 100; // Convert to percentage for display
                                response.MinerFeeSats = chainLimits.MinerFee;
                                response.EstimatedFeeSats = (long)Math.Ceiling(amount * chainLimits.FeePercentage) + chainLimits.MinerFee;
                                response.FeeDescription = $"{chainLimits.FeePercentage * 100:F2}% + {chainLimits.MinerFee} sats miner fee";
                                return Json(response);
                            }
                        }
                        // Outside chain-swap limits (or unavailable): fall through to the Batch fee estimate.
                    }
                }
            }

            // Ark intent/transaction fees - need to get coins and build outputs
            var isAutoMode = string.Equals(request.CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);
            List<ArkCoin> coins;

            if (isAutoMode)
            {
                // Auto mode: use smart coin selection based on destination type
                var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);
                var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
                var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
                var availableCoins = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();

                if (!availableCoins.Any())
                {
                    response.Error = "No spendable coins available";
                    return Json(response);
                }

                // Determine destination type for smart selection
                var destType = DestinationType.ArkAddress; // default: consolidation / ark send
                long? targetSats = null;

                if (request.Outputs.Any(o => !string.IsNullOrWhiteSpace(o.Destination)))
                {
                    var firstDest = request.Outputs.First(o => !string.IsNullOrWhiteSpace(o.Destination)).Destination!.Trim();
                    if (IsLightningDestination(firstDest))
                        destType = DestinationType.LightningInvoice;
                    else if (firstDest.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("1") || firstDest.StartsWith("3"))
                        destType = DestinationType.BitcoinAddress;

                    // Calculate target amount
                    var amounts = request.Outputs.Where(o => o.AmountSats.HasValue).Select(o => o.AmountSats!.Value).ToList();
                    if (amounts.Any())
                        targetSats = amounts.Sum();
                }

                // Reuse the same selection logic as SuggestCoins
                var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
                var recoverable = availableCoins.Where(c => c.Swept).ToList();
                SuggestCoinsResponse suggestion;

                if (destType == DestinationType.LightningInvoice)
                {
                    suggestion = SelectCoins(nonRecoverable.Any() ? nonRecoverable : availableCoins, targetSats, SpendType.Swap);
                }
                else if (destType == DestinationType.BitcoinAddress)
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else if (string.Equals(request.SpendType, "Batch", StringComparison.OrdinalIgnoreCase))
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else
                {
                    // Ark address / offchain: prefer non-recoverable
                    suggestion = nonRecoverable.Any()
                        ? SelectCoins(nonRecoverable, targetSats, SpendType.Offchain)
                        : SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }

                if (suggestion.Error != null)
                {
                    response.Error = suggestion.Error;
                    return Json(response);
                }

                // Map selected outpoints back to coins
                var selectedSet = suggestion.SuggestedOutpoints.ToHashSet();
                coins = availableCoins.Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}")).ToList();

                // Populate response with selected coin info
                response.TotalInputSats = coins.Sum(c => c.TxOut.Value.Satoshi);
                response.SelectedCoinCount = coins.Count;
                response.SelectedOutpoints = suggestion.SuggestedOutpoints;

                request.TotalInputSats = response.TotalInputSats;
            }
            else
            {
                coins = ArkSpendHelpers.ResolveCoinsForOutpoints(
                    await arkadeSpender.GetAvailableCoins(config!.WalletId!, token),
                    request.VtxoOutpoints);
            }

            if (coins.Count == 0)
            {
                response.Error = "No valid coins found for selected outpoints";
                return Json(response);
            }

            var outputs = new List<ArkTxOut>();
            foreach (var outputReq in request.Outputs)
            {
                if (string.IsNullOrWhiteSpace(outputReq.Destination)) continue;

                var parseResult = ArkSpendHelpers.ParseOutputDestination(outputReq.Destination, serverInfo.Network);
                if (parseResult.Destination == null) continue;

                var amount = outputReq.AmountSats.HasValue
                    ? Money.Satoshis(outputReq.AmountSats.Value)
                    : (request.Outputs.Count == 1 ? Money.Satoshis(request.TotalInputSats) : Money.Zero);

                if (amount > Money.Zero)
                {
                    outputs.Add(new ArkTxOut(parseResult.OutputType, amount, parseResult.Destination));
                }
            }

            // If no outputs specified, this is a consolidation (send to self)
            // For fee estimation, we use a placeholder - fee is based on input/output amounts and types
            if (outputs.Count == 0)
            {
                var totalInput = coins.Sum(c => c.TxOut.Value);
                // Use first coin's contract address as placeholder for fee estimation
                // The actual destination will be derived at spend time
                var placeholderDest = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, totalInput, placeholderDest));
            }

            // For batch with on-chain outputs, include a change VTXO output for accurate fee estimation
            var hasOnchain = outputs.Any(o => o.Type == ArkTxOutType.Onchain);
            var totalOutputSats = outputs.Sum(o => o.Value.Satoshi);
            var totalCoinsSats = coins.Sum(c => c.TxOut.Value.Satoshi);
            if (hasOnchain && totalCoinsSats > totalOutputSats)
            {
                var changePlaceholder = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(totalCoinsSats - totalOutputSats), changePlaceholder));
            }

            // Estimate the fee — Arkade (offchain) sends have no fee, only Batch intents do
            if (string.Equals(request.SpendType, "Arkade", StringComparison.OrdinalIgnoreCase) && !hasOnchain)
            {
                response.EstimatedFeeSats = 0;
                response.FeeDescription = "No fee for Arkade transactions";
            }
            else
            {
                var estimatedFee = await feeEstimator.EstimateFeeAsync(coins.ToArray(), outputs.ToArray(), token);
                response.EstimatedFeeSats = estimatedFee;
                response.FeeDescription = hasOnchain ? "Batch transaction fee" : "Arkade service fee";
            }

            return Json(response);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fee estimation failed for store {StoreId}", storeId);
            return Json(new FeeEstimateResponse { Error = DescribeArkError(ex, "Fee estimation failed") });
        }
    }

    /// <summary>
    /// Parse a destination string server-side (BIP21, Lightning, Ark address).
    /// Used by Send wizard AJAX for rich destination display.
    /// </summary>
    [HttpPost("stores/{storeId}/parse-destination")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ParseDestination(
        string storeId,
        [FromBody] ParseDestinationRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return BadRequest("Invalid store settings");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = await ParseSendDestinationAsync(request.Destination, request.AmountBtc, serverInfo.Network, token);

            return Json(new ParseDestinationResponse
            {
                RawBip21 = parsed.RawDestination,
                ResolvedAddress = parsed.ResolvedAddress,
                Type = parsed.Type.ToString(),
                TypeBadge = parsed.TypeBadge,
                TypeBadgeClass = parsed.TypeBadgeClass,
                AmountSats = parsed.AmountSats,
                AmountBtc = parsed.AmountBtc,
                PayoutId = parsed.PayoutId,
                IsValid = parsed.IsValid,
                Error = parsed.Error,
                IsBip21 = parsed.Type is SendDestinationType.Bip21Ark or SendDestinationType.Bip21Lightning
                          || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase),
                IsLightning = parsed.Type is SendDestinationType.LightningInvoice or SendDestinationType.Bip21Lightning
                              or SendDestinationType.Lnurl,
                IsLnurl = parsed.Type == SendDestinationType.Lnurl,
                LnurlMinSats = parsed.LnurlMinSats,
                LnurlMaxSats = parsed.LnurlMaxSats,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Destination parsing failed for store {StoreId}", storeId);
            return Json(new ParseDestinationResponse { Error = DescribeArkError(ex, "Could not parse destination") });
        }
    }

    /// <summary>
    /// Suggests optimal coin selection based on destination type and amount.
    /// </summary>
    [HttpPost("stores/{storeId}/suggest-coins")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SuggestCoins(
        string storeId,
        [FromBody] SuggestCoinsRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null)
            return Json(new SuggestCoinsResponse { Error = "Store not configured" });

        if (!ModelState.IsValid)
            return Json(new SuggestCoinsResponse { Error = "Invalid request" });

        try
        {
            var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);

            // Exclude VTXOs locked by pending intents
            var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
            var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);

            // Filter out excluded outpoints and locked VTXOs
            var excludeSet = request.ExcludeOutpoints?
                .Select(o => o.Trim())
                .ToHashSet() ?? new HashSet<string>();

            var availableCoins = allCoins
                .Where(c => !lockedSet.Contains(c.Outpoint) && !excludeSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
                .ToList();

            if (!availableCoins.Any())
            {
                return Json(new SuggestCoinsResponse { Error = "No spendable coins available" });
            }

            // Separate by recoverability
            var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
            var recoverable = availableCoins.Where(c => c.Swept).ToList();

            var response = new SuggestCoinsResponse();

            // Lightning requires non-recoverable coins only
            if (request.DestinationType == DestinationType.LightningInvoice)
            {
                if (!nonRecoverable.Any())
                {
                    return Json(new SuggestCoinsResponse
                    {
                        Error = "Lightning requires non-recoverable coins. No non-recoverable coins available."
                    });
                }

                response = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Swap);
            }
            // Ark address: prefer offchain (non-recoverable), fallback to batch (recoverable)
            else if (request.DestinationType == DestinationType.ArkAddress)
            {
                // Try offchain first with non-recoverable
                if (nonRecoverable.Any())
                {
                    var offchainAttempt = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Offchain);
                    if (offchainAttempt.Error == null)
                    {
                        response = offchainAttempt;
                    }
                    else if (recoverable.Any())
                    {
                        // Fallback to batch with all coins
                        response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
                        response.Warning = "Using batch mode (recoverable coins included)";
                    }
                    else
                    {
                        response = offchainAttempt; // Return the error
                    }
                }
                else if (recoverable.Any())
                {
                    // Only recoverable available - must use batch
                    response = SelectCoins(recoverable, request.AmountSats, SpendType.Batch);
                    response.Warning = "Offchain not available - only recoverable coins";
                }
                else
                {
                    response.Error = "No spendable coins available";
                }
            }
            // Bitcoin address: always batch
            else
            {
                response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
            }

            return Json(response);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Coin selection failed for store {StoreId}", storeId);
            return Json(new SuggestCoinsResponse { Error = DescribeArkError(ex, "Coin selection failed") });
        }
    }

    /// <summary>
    /// Unified Send Wizard - main entry point.
    /// </summary>
    [HttpGet("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        string? vtxos,
        string? destinations,
        string? destination,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null)
            return errorResult;

        var model = new SendWizardViewModel
        {
            StoreId = storeId
        };

        // Load balances
        model.Balances = await walletService.GetBalances(config!.WalletId!, token);

        // Load available (spendable) coins - get outpoints from ArkCoin, then fetch ArkVtxo details
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);

        // Exclude VTXOs locked by pending intents
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var spendableOutpoints = allCoins
            .Where(c => !lockedSet.Contains(c.Outpoint))
            .Select(c => c.Outpoint).ToList();

        if (!spendableOutpoints.Any())
            return View("Send", model);

        // Fetch full ArkVtxo details for the spendable coins
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        if (!model.AvailableVtxos.Any())
            return View("Send", model);

        // Handle pre-selected VTXOs from query param
        if (!string.IsNullOrEmpty(vtxos))
        {
            if (vtxos.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // Special case: select all available VTXOs
                model.SelectedVtxos = model.AvailableVtxos.ToList();
                model.CoinSelectionMode = "manual";
            }
            else
            {
                var requestedOutpoints = vtxos.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToHashSet();

                model.SelectedVtxos = model.AvailableVtxos
                    .Where(v => requestedOutpoints.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
                    .ToList();

                model.CoinSelectionMode = "manual";

                // Warn if some requested coins unavailable
                if (model.SelectedVtxos.Count < requestedOutpoints.Count)
                {
                    var found = model.SelectedVtxos
                        .Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}")
                        .ToHashSet();
                    var missing = requestedOutpoints.Except(found).Count();
                    model.Errors.Add($"{missing} selected coin(s) no longer available");
                }
            }
        }

        // Handle pre-filled destinations (BIP21-aware parsing)
        if (!string.IsNullOrEmpty(destinations))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsedDestinations = ParseDestinationsParam(destinations, serverInfo.Network);

            foreach (var parsed in parsedDestinations)
            {
                var isBip21 = parsed.Type is SendDestinationType.Bip21Ark or SendDestinationType.Bip21Lightning
                              || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
                var output = new SendOutputViewModel
                {
                    Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                    RawBip21 = isBip21 ? parsed.RawDestination : null,
                    ResolvedAddress = parsed.ResolvedAddress,
                    AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                    PayoutId = parsed.PayoutId,
                    IsBip21Parsed = isBip21,
                    IsReadonly = isBip21,
                    DetectedType = MapSendTypeToDestinationType(parsed.Type),
                    Error = parsed.Error
                };
                model.Outputs.Add(output);
            }
        }
        else if (!string.IsNullOrEmpty(destination))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = ParseSendDestination(destination, null, serverInfo.Network);
            var isBip21 = parsed.Type is SendDestinationType.Bip21Ark or SendDestinationType.Bip21Lightning
                          || destination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
            var isLightning = parsed.Type is SendDestinationType.LightningInvoice or SendDestinationType.Bip21Lightning;

            model.Outputs.Add(new SendOutputViewModel
            {
                Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                RawBip21 = isBip21 ? destination : null,
                ResolvedAddress = parsed.ResolvedAddress,
                AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                PayoutId = parsed.PayoutId,
                IsBip21Parsed = isBip21,
                IsReadonly = isBip21 || isLightning,
                DetectedType = MapSendTypeToDestinationType(parsed.Type),
                Error = parsed.Error
            });
        }
        else
        {
            // Default: one empty output row
            model.Outputs.Add(new SendOutputViewModel());
        }

        return View("Send", model);
    }

    /// <summary>
    /// Execute the send transaction.
    /// </summary>
    [HttpPost("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        [FromForm] SendWizardViewModel model,
        [FromForm] string[] selectedVtxoOutpoints,
        [FromForm] string? SpendType,
        [FromForm] string? CoinSelectionMode,
        CancellationToken token)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null)
            return errorResult;

        model.StoreId = storeId;
        model.Balances = await walletService.GetBalances(config!.WalletId!, token);

        // User's spend type preference (Arkade = offchain, Batch = onchain intent)
        var preferBatch = string.Equals(SpendType, "Batch", StringComparison.OrdinalIgnoreCase);

        // Re-load available coins for validation (excluding locked VTXOs)
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var unlocked = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();
        var spendableOutpoints = unlocked.Select(c => c.Outpoint).ToList();
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        // Validate selected coins
        var isAutoMode = string.Equals(CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);

        if (!selectedVtxoOutpoints.Any() && !isAutoMode)
        {
            model.Errors.Add("No coins selected");
            return View("Send", model);
        }

        var selectedSet = selectedVtxoOutpoints.ToHashSet();
        var selectedCoins = unlocked
            .Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
            .ToList();

        if (selectedCoins.Count != selectedVtxoOutpoints.Length && isAutoMode)
        {
            // Auto mode: re-select coins from available unlocked set
            selectedCoins = unlocked.ToList();
            selectedSet = selectedCoins
                .Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}")
                .ToHashSet();
        }
        else if (selectedCoins.Count != selectedVtxoOutpoints.Length)
        {
            var missing = selectedVtxoOutpoints.Length - selectedCoins.Count;
            model.Errors.Add($"{missing} selected coin(s) are no longer available (spent or locked). Please re-select your coins and try again.");
            return View("Send", model);
        }

        if (!selectedCoins.Any())
        {
            model.Errors.Add("No coins available to spend");
            return View("Send", model);
        }

        model.SelectedVtxos = model.AvailableVtxos
            .Where(v => selectedSet.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
            .ToList();

        // Validate outputs - allow empty for consolidation
        var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();
        var isConsolidation = !validOutputs.Any();

        // Handle consolidation (no destination = send to self)
        if (isConsolidation)
        {
            try
            {
                var consolidationServerInfo = await clientTransport.GetServerInfoAsync(token);
                var consolidationTotalInput = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
                var hasRecoverableCoins = selectedCoins.Any(c => c.Swept);

                // Prevent pointless 1-in-1-out Arkade consolidation
                // With Arkade (not Batch) and only 1 non-recoverable coin, consolidation does nothing useful
                if (!preferBatch && !hasRecoverableCoins && selectedCoins.Count == 1)
                {
                    model.Errors.Add("Arkade consolidation with a single coin is not useful. Either select multiple coins to consolidate, use Batch mode to renew expiry, or enter a destination to send funds.");
                    return View("Send", model);
                }

                // Get the wallet's own Ark address for consolidation
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.Inactive, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // For recoverable coins OR user chose Batch, create an intent (batch transaction)
                if (hasRecoverableCoins || preferBatch)
                {
                    // Estimate fee for batch transaction
                    var consolidationOutputForFee = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(consolidationTotalInput),
                        selfDest);
                    var feeEstimation = await feeEstimator.EstimateFeeAsync(
                        selectedCoins.ToArray(),
                        new[] { consolidationOutputForFee },
                        token);

                    var outputAmount = consolidationTotalInput - feeEstimation;
                    if (outputAmount <= 0)
                    {
                        model.Errors.Add("Insufficient funds after fees");
                        return View("Send", model);
                    }

                    var consolidationOutput = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(outputAmount),
                        selfDest);

                    // Create intent for batch (automatically cancels any overlapping intents)
                    var intentTxId = await intentGenerationService.GenerateManualIntent(
                        config.WalletId!,
                        new ArkIntentSpec(
                            selectedCoins.ToArray(),
                            new [] { consolidationOutput },
                            null,
                            null
                        ),
                        cancellationToken: token);

                    var message = hasRecoverableCoins
                        ? $"Recovery intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round."
                        : $"Batch intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round.";

                    return RedirectWithSuccess(nameof(Intents), message, new { storeId });
                }

                // For non-recoverable coins with Arkade preference, use direct Arkade spend
                var arkadeOutput = new ArkTxOut(
                    ArkTxOutType.Vtxo,
                    Money.Satoshis(consolidationTotalInput),
                    selfDest);

                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    selectedCoins.ToArray(),
                    new[] { arkadeOutput },
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), token);

                return RedirectWithSuccess(nameof(StoreOverview), $"Coins consolidated successfully! TxId: {txId}", new { storeId });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Consolidation failed for store {StoreId}", storeId);
                model.Errors.Add(DescribeArkError(ex, "Consolidation failed"));
                return View("Send", model);
            }
        }

        // Get server info for network (needed for Lightning and destination parsing)
        var serverInfo = await clientTransport.GetServerInfoAsync(token);

        // Check for Lightning (BOLT11, LNURL, or Lightning Address)
        var isLightning = validOutputs.Any(o => IsLightningDestination(o.Destination));

        if (isLightning)
        {
            if (validOutputs.Count > 1)
            {
                model.Errors.Add("Lightning supports single output only");
                return View("Send", model);
            }

            if (selectedCoins.Any(c => c.Swept))
            {
                model.Errors.Add("Lightning requires non-recoverable coins");
                return View("Send", model);
            }

            // Execute Lightning payment
            try
            {
                var lnOutput = validOutputs[0];
                var lnDestination = lnOutput.Destination;

                // Resolve LNURL/Lightning Address to BOLT11 at submit time
                if (lnDestination.IsValidEmail() ||
                    lnDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = lnOutput.AmountSats ?? model.TotalSelectedSats;
                    var (bolt11, lnurlError) = await ResolveLnurlToInvoiceAsync(
                        lnDestination, amount, serverInfo.Network, token);
                    if (lnurlError != null)
                    {
                        model.Errors.Add($"LNURL resolution failed: {lnurlError}");
                        return View("Send", model);
                    }
                    lnDestination = bolt11!;
                }
                else
                {
                    lnDestination = lnDestination
                        .Replace("lightning:", "", StringComparison.OrdinalIgnoreCase);
                }

                // A Lightning send settles through a submarine swap, so a payout it fulfills
                // stays InProgress carrying the swap id until ArkPayoutSettlementListener
                // completes it once the swap settles.
                var lnError = await SpendFulfillingPayouts([lnOutput],
                    ct => arkadeSpendingService.Spend(store!, lnDestination, ct), token);
                if (lnError != null)
                {
                    model.Errors.Add(lnError);
                    return View("Send", model);
                }

                return RedirectWithSuccess(nameof(StoreOverview), "Lightning payment sent!", new { storeId });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lightning payment failed for store {StoreId}", storeId);
                model.Errors.Add(DescribeArkError(ex, "Lightning payment failed"));
                return View("Send", model);
            }
        }

        // Parse all destinations and build ArkTxOut array
        var totalInputAmount = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
        var arkOutputs = new List<ArkTxOut>();

        for (int i = 0; i < validOutputs.Count; i++)
        {
            var output = validOutputs[i];
            var (dest, parsedAmount, outputType) = ArkSpendHelpers.ParseOutputDestination(output.Destination, serverInfo.Network);

            if (dest == null)
            {
                output.Error = "Invalid address format";
                model.Errors.Add($"Output {i + 1}: Invalid address format");
                continue;
            }

            // Amount priority: user-specified > destination-specified > (single output: send all)
            var outputAmount = output.AmountSats.HasValue
                ? Money.Satoshis(output.AmountSats.Value)
                : parsedAmount;

            if (outputAmount == null || outputAmount == Money.Zero)
            {
                if (validOutputs.Count == 1)
                {
                    // Single output with no amount - send all
                    outputAmount = Money.Satoshis(totalInputAmount);
                }
                else
                {
                    output.Error = "Amount is required";
                    model.Errors.Add($"Output {i + 1}: Amount is required");
                    continue;
                }
            }

            arkOutputs.Add(new ArkTxOut(outputType, outputAmount, dest));
        }

        if (model.Errors.Any())
        {
            return View("Send", model);
        }

        // On-chain (bitcoin) destination handling:
        //  - Arkade mode, a single output, and an amount within the chain-swap limits → settle via
        //    an ARK→BTC chain swap (same mechanism as a Lightning send), initiated instantly.
        //  - Anything the swap can't take (Batch chosen, multiple outputs, Boltz unavailable, or an
        //    amount outside the swap limits) falls through to the batch path below.
        var hasOnchainOutput = arkOutputs.Any(o => o.Type == ArkTxOutType.Onchain);

        if (hasOnchainOutput && !preferBatch && arkOutputs.Count == 1)
        {
            var settlementSats = arkOutputs[0].Value.Satoshi;
            var chainLimits = boltzLimitsValidator is null
                ? null
                : await boltzLimitsValidator.GetChainLimitsAsync(isBtcToArk: false, token);
            var withinChainSwapLimits = chainLimits is not null
                && settlementSats >= chainLimits.MinAmount
                && settlementSats <= chainLimits.MaxAmount;

            if (withinChainSwapLimits)
            {
                try
                {
                    var btcOutput = validOutputs[0];
                    // Chain swaps run their own coin selection, so the selected VTXOs are not passed as explicit inputs here.
                    // A chain swap only *initiates* the transfer, so a payout it fulfills stays
                    // InProgress (carrying the swap id) until it settles; it must not be marked
                    // delivered yet.
                    var swapError = await SpendFulfillingPayouts([btcOutput],
                        ct => arkadeSpendingService.Spend(store!, btcOutput.Destination, settlementSats, null, ct), token);
                    if (swapError != null)
                    {
                        model.Errors.Add(swapError);
                        return View("Send", model);
                    }

                    return RedirectWithSuccess(nameof(StoreOverview),
                        "Bitcoin settlement initiated via chain swap. Funds arrive once the swap settles.",
                        new { storeId });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Bitcoin settlement failed for store {StoreId}", storeId);
                    model.Errors.Add(DescribeArkError(ex, "Bitcoin settlement failed"));
                    return View("Send", model);
                }
            }
            // Outside the chain-swap limits (or Boltz unavailable) → fall through to batch.
        }

        // Batch settles on-chain outputs the chain swap didn't take, plus any explicit Batch send.
        var useBatch = preferBatch || hasOnchainOutput;

        // Execute the spend
        try
        {
            if (useBatch)
            {
                // Batch path: create an intent for the next batch round
                // Need to add a change output back to self for the remainder after fees
                var totalOutput = arkOutputs.Sum(o => o.Value.Satoshi);

                // Build preliminary outputs to estimate fees (include a placeholder change output)
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // Estimate fees with all outputs including change
                var preliminaryOutputs = arkOutputs.ToList();
                var preliminaryChange = totalInputAmount - totalOutput;
                if (preliminaryChange > 0)
                {
                    preliminaryOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(preliminaryChange), selfDest));
                }

                var feeEstimation = await feeEstimator.EstimateFeeAsync(
                    selectedCoins.ToArray(),
                    preliminaryOutputs.ToArray(),
                    token);

                var changeAmount = totalInputAmount - totalOutput - feeEstimation;
                if (changeAmount < 0)
                {
                    model.Errors.Add($"Insufficient funds. Need {totalOutput + feeEstimation} sats but only have {totalInputAmount} sats.");
                    return View("Send", model);
                }

                // Build final outputs: destination(s) + change (if any)
                var finalOutputs = arkOutputs.ToList();
                if (changeAmount > 0)
                {
                    finalOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(changeAmount), selfDest));
                }

                var intentSpec = new ArkIntentSpec(
                    selectedCoins.ToArray(),
                    finalOutputs.ToArray(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddHours(1)
                );

                // A batch intent is only a *commitment* to the next batch round — nothing has
                // settled yet. Payouts it fulfills stay InProgress carrying the intent tx id;
                // ArkPayoutSettlementListener completes them when the batch commits, or
                // reverts them if the intent is cancelled, expires, or its batch fails.
                string? intentTxId = null;
                var batchError = await SpendFulfillingPayouts(validOutputs, async ct =>
                {
                    intentTxId = await intentGenerationService.GenerateManualIntent(
                        config.WalletId!, intentSpec, cancellationToken: ct);
                    return new SpendResult(TxId: null, SwapId: null, IntentTxId: intentTxId);
                }, token);
                if (batchError != null)
                {
                    model.Errors.Add(batchError);
                    return View("Send", model);
                }

                return RedirectWithSuccess(nameof(Intents),
                    $"Batch intent created! Intent ID: {intentTxId}. Transaction will be included in the next batch round.",
                    new { storeId });
            }
            else
            {
                // Arkade path: instant offchain spend. Payouts fulfilled by outputs are
                // completed immediately with the real txid.
                uint256? sentTxId = null;
                var sendError = await SpendFulfillingPayouts(validOutputs, async ct =>
                {
                    sentTxId = await arkadeSpender.Spend(
                        config.WalletId!,
                        selectedCoins.ToArray(),
                        arkOutputs.ToArray(),
                        ct);

                    // Poll for VTXO updates
                    var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: ct);
                    await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), ct);

                    return new SpendResult(sentTxId.ToString(), null);
                }, token);
                if (sendError != null)
                {
                    model.Errors.Add(sendError);
                    return View("Send", model);
                }

                return RedirectWithSuccess(nameof(StoreOverview), $"Transaction sent successfully! TxId: {sentTxId}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Send failed for store {StoreId}", storeId);
            model.Errors.Add(DescribeArkError(ex, "Transaction failed"));
            return View("Send", model);
        }
    }

    private static SuggestCoinsResponse SelectCoins(
        List<ArkCoin> coins,
        long? targetSats,
        SpendType spendType)
        => ArkSpendHelpers.SelectCoins(coins, targetSats, spendType);

    private async Task<InitialWalletSetupViewModel> CreateInitialWalletSetupViewModel(
        StoreData store,
        InitialWalletSetupViewModel? model = null,
        CancellationToken cancellationToken = default)
    {
        model ??= new InitialWalletSetupViewModel();
        model.SettlementOptions = await CreateSettlementOptions(
            store,
            new ArkadePaymentMethodConfig(string.Empty),
            model.SettlementInputs,
            cancellationToken);
        return model;
    }

    private async Task<IReadOnlyList<SettlementOptionModel>> CreateSettlementOptions(
        StoreData store,
        ArkadePaymentMethodConfig config,
        IReadOnlyDictionary<StoreSettlementOption, SettlementInput>? inputs = null,
        CancellationToken cancellationToken = default)
    {
        var viewModels = new List<SettlementOptionModel>();
        foreach (var option in settlementOptions)
        {
            viewModels.Add(await option.CreateViewModel(
                store,
                config,
                inputs?.GetValueOrDefault(option.Type),
                cancellationToken));
        }

        return viewModels;
    }

    private Dictionary<StoreSettlementOption, SettlementInput> ReadSettlementInputsFromForm()
    {
        var inputs = new Dictionary<StoreSettlementOption, SettlementInput>();
        if (!Request.HasFormContentType)
            return inputs;

        var form = Request.Form;
        foreach (var option in settlementOptions)
        {
            var prefix = SettlementInputName.Prefix(option.Type);
            var data = new JObject();
            foreach (var field in form)
            {
                var dataKey = ReadDataKey(field.Key, prefix);
                if (dataKey is null)
                    continue;

                data[dataKey] = field.Value.ToString();
            }

            if (data.HasValues)
                inputs[option.Type] = new SettlementInput { Data = data };
        }

        return inputs;
    }

    private static string? ReadDataKey(string fieldName, string prefix)
    {
        if (!fieldName.StartsWith($"{prefix}[", StringComparison.Ordinal) ||
            !fieldName.EndsWith(']'))
        {
            return null;
        }

        var keyStart = prefix.Length + 1;
        var keyLength = fieldName.Length - keyStart - 1;
        return keyLength > 0 ? fieldName.Substring(keyStart, keyLength) : null;
    }

    [HttpPost("stores/{storeId}/update-wallet-config")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> UpdateWalletConfig(string storeId, StoreSettingsFormModel model, string? command = null, string? returnTo = null, CancellationToken cancellationToken = default)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;
        var storeData = store!;
        var arkConfig = config!;
        model.SettlementInputs = ReadSettlementInputsFromForm();

        if (command == "mark-wallet-backed-up")
        {
            var newConfig = arkConfig with { WalletBackedUp = true };
            storeData.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
            await storeRepository.UpdateStore(storeData);

            var returnAction = returnTo == "overview" ? nameof(StoreOverview) : nameof(Settings);
            return RedirectWithSuccess(returnAction, "Wallet marked as backed up.", new { storeId });
        }

        if (command == "toggle-subdust")
        {
            var newConfig = arkConfig with { AllowSubDustAmounts = !arkConfig.AllowSubDustAmounts };
            storeData.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
            await storeRepository.UpdateStore(storeData);
            return RedirectWithSuccess(nameof(Settings),
                newConfig.AllowSubDustAmounts ? "Sub-dust amounts enabled for Arkade payments." : "Sub-dust amounts disabled for Arkade payments.",
                new { storeId });
        }

        if (!string.IsNullOrEmpty(command) &&
            settlementOptions.FirstOrDefault(option => option.HandlesCommand(command)) is { } settlementOption)
        {
            var result = await settlementOption.HandleCommand(
                command,
                storeData,
                arkConfig,
                model.SettlementInputs.GetValueOrDefault(settlementOption.Type),
                cancellationToken);
            if (!result.Success || result.Config is null)
                return RedirectWithError(nameof(Settings), result.Message, new { storeId });

            storeData.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], result.Config);
            await storeRepository.UpdateStore(storeData);
            await settlementOption.OnSaved(result.Config, cancellationToken);
            return RedirectWithSuccess(nameof(Settings), result.Message, new { storeId });
        }

        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpGet("stores/{storeId}/contracts")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Contracts(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool debug = false)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Get status filter using helper
        var activeFilter = ParseBooleanFilter(searchTerm, "status", "active");

        // Get contracts with pagination
        var contracts = await contractStorage.GetContracts(
            walletIds: [config!.WalletId],
            isActive: activeFilter,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs for the contracts (include spent and recoverable for full history)
        var contractVtxos = new Dictionary<string, ArkVtxo[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToList();
            var vtxos = await vtxoStorage.GetVtxos(
                scripts: contractScripts,
                walletIds: [config.WalletId],
                includeSpent: true,
                cancellationToken: HttpContext.RequestAborted);

            contractVtxos = vtxos
                .GroupBy(v => v.Script)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        // Always load swaps
        var contractSwaps = new Dictionary<string, NArk.Swaps.Models.ArkSwap[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToArray();
            var swaps = await swapStorage.GetSwaps(
                walletIds: [config.WalletId!],
                contractScripts: contractScripts,
                cancellationToken: HttpContext.RequestAborted);
            contractSwaps = swaps
                .GroupBy(s => s.ContractScript)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        var model = new StoreContractsViewModel
        {
            StoreId = storeId,
            Contracts = contracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            ContractVtxos = contractVtxos,
            ContractSwaps = contractSwaps,
            Debug = debug,
            CachedContractScripts = (await contractStorage.GetContracts(walletIds: [config.WalletId], isActive: true, cancellationToken: HttpContext.RequestAborted))
                .Select(c => c.Script).ToHashSet(),
            ListenedScripts = debug ? vtxoSyncService.ListenedScripts.ToHashSet() : []
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/swaps")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Swaps(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Status filter: multi-select like the VTXO page. Without an explicit choice,
        // hide Failed swaps — expired unpaid Lightning invoices pile up there as noise.
        var search = new SearchString(searchTerm);
        if (!search.ContainsFilter("status"))
        {
            searchTerm = "status:pending,status:settled,status:refunded";
            search = new SearchString(searchTerm);
        }

        var statusFilter = search.GetFilterArray("status")
            .SelectMany(MapSwapStatusFilter)
            .Distinct()
            .ToArray();

        // Get type filter using helper
        var typeFilter = ParseEnumFilter<ArkSwapType>(searchTerm, "type", t => t switch
        {
            "lightning-to-arkade" => ArkSwapType.ReverseSubmarine,
            "arkade-to-lightning" => ArkSwapType.Submarine,
            "arkade-to-bitcoin" => ArkSwapType.ChainArkToBtc,
            "bitcoin-to-arkade" => ArkSwapType.ChainBtcToArk,
            "reverse" => ArkSwapType.ReverseSubmarine,
            "submarine" => ArkSwapType.Submarine,
            _ => null
        });

        var swaps = await swapStorage.GetSwaps(
            walletIds: [config!.WalletId!],
            status: statusFilter.Length > 0 ? statusFilter : null,
            swapTypes: typeFilter != null ? [typeFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get contracts for the swaps to display contract details
        var swapContractScripts = swaps.Select(s => s.ContractScript).Distinct().ToArray();
        var swapContracts = await contractStorage.GetContracts(
            walletIds: [config.WalletId!],
            scripts: swapContractScripts,
            cancellationToken: HttpContext.RequestAborted);

        var model = new StoreSwapsViewModel
        {
            StoreId = storeId,
            Swaps = swaps,
            SwapContracts = swapContracts.ToDictionary(c => c.Script),
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm)
        };

        return View(model);
    }

    [HttpPost("stores/{storeId}/swaps/{swapId}/poll")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> PollSwap(string storeId, string swapId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            if (boltzClient == null)
                return RedirectWithError(nameof(Swaps), "Boltz client is not configured", new { storeId });

            var swaps = await swapStorage.GetSwaps(
                walletIds: [config!.WalletId!],
                swapIds: [swapId],
                cancellationToken: HttpContext.RequestAborted);
            var swap = swaps.FirstOrDefault();
            if (swap == null)
                return RedirectWithError(nameof(Swaps), $"Swap {swapId} not found.", new { storeId });

            var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, HttpContext.RequestAborted);
            var newStatus = MapBoltzStatus(statusResponse.Status, swap.Status);

            if (swap.Status != newStatus)
            {
                await swapStorage.UpdateSwapStatus(config.WalletId!, swapId, newStatus, cancellationToken: HttpContext.RequestAborted);
                return RedirectWithSuccess(nameof(Swaps), $"Swap {swapId} polled successfully. Status updated to: {newStatus}", new { storeId });
            }

            return RedirectWithSuccess(nameof(Swaps), $"Swap {swapId} polled successfully. No status change (current: {swap.Status}).", new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to poll swap {SwapId}", swapId);
            return RedirectWithError(nameof(Swaps), $"Failed to poll swap {swapId}. Check the Boltz connection and try again.", new { storeId });
        }
    }

    // Only genuinely terminal Boltz statuses change the stored status; mid-flight
    // and unrecognized statuses keep it, so polling a healthy pending swap never
    // poisons it with Unknown.
    private static ArkSwapStatus MapBoltzStatus(string status, ArkSwapStatus currentStatus)
    {
        return BoltzSwapStatus.ToArkSwapStatus(status) ?? currentStatus;
    }

    private static ArkSwapStatus[] MapSwapStatusFilter(string filter) => filter switch
    {
        // Unknown rides with Pending: the rest of the plugin treats it as in-flight.
        "pending" => [ArkSwapStatus.Pending, ArkSwapStatus.Unknown],
        "settled" => [ArkSwapStatus.Settled],
        "refunded" => [ArkSwapStatus.Refunded],
        "failed" => [ArkSwapStatus.Failed],
        _ => []
    };

    private static long? GetSettledSwapFeeSats(
        ArkSwapType swapType,
        long expectedAmountSats,
        string invoice,
        string? metadataJson,
        Network network)
    {
        return swapType switch
        {
            ArkSwapType.Submarine => Bolt11Helper.TryGetAmountSats(invoice, network) is { } invoiceAmountSats
                ? Math.Max(0, expectedAmountSats - invoiceAmountSats)
                : null,
            ArkSwapType.ReverseSubmarine => Bolt11Helper.TryGetAmountSats(invoice, network) is { } invoiceAmountSats
                ? Math.Max(0, invoiceAmountSats - expectedAmountSats)
                : null,
            ArkSwapType.ChainBtcToArk or ArkSwapType.ChainArkToBtc =>
                GetChainSwapAmounts(expectedAmountSats, metadataJson).FeesPaidSats,
            _ => null
        };
    }

    private static (long SourceAmountSats, long? DestinationAmountSats, long? FeesPaidSats)
        GetChainSwapAmounts(long expectedAmountSats, string? metadataJson)
    {
        var boltzResponse = TryGetChainResponse(metadataJson);
        var sourceAmountSats = boltzResponse?.LockupDetails?.Amount > 0
            ? boltzResponse.LockupDetails.Amount
            : expectedAmountSats;
        var destinationAmountSats = boltzResponse?.ClaimDetails?.Amount > 0
            ? boltzResponse.ClaimDetails.Amount
            : (long?)null;
        var feesPaidSats = destinationAmountSats.HasValue
            ? Math.Max(0, sourceAmountSats - destinationAmountSats.Value)
            : (long?)null;

        return (sourceAmountSats, destinationAmountSats, feesPaidSats);
    }

    private static ChainResponse? TryGetChainResponse(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            return metadata?.TryGetValue(SwapMetadata.BoltzResponse, out var raw) == true
                ? JsonSerializer.Deserialize<ChainResponse>(raw)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetSettlementSubtext(
        ArkSwapStatus status,
        long? feesPaidSats)
    {
        if (status == ArkSwapStatus.Settled && feesPaidSats.HasValue)
            return $"Fees {feesPaidSats.Value:N0} sats";

        if (status is ArkSwapStatus.Pending or ArkSwapStatus.Unknown)
            return "Settlement in progress";

        if (status == ArkSwapStatus.Failed)
            return "Settlement failed before completion";

        if (status == ArkSwapStatus.Refunded)
            return "Settlement refunded";

        return null;
    }

    [HttpGet("stores/{storeId}/vtxos")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Vtxos(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Parse status filters - default to unspent and recoverable if no filter is set
        var search = new SearchString(searchTerm);
        bool includeSpent = false;
        bool filterRecoverableOnly = false;
        bool filterNonRecoverableOnly = false;
        bool? spendableFilter = null; // null = all, true = spendable only, false = non-spendable only

        if (search.ContainsFilter("status"))
        {
            var statusFilters = search.GetFilterArray("status");
            includeSpent = statusFilters.Contains("spent");
            var hasRecoverable = statusFilters.Contains("recoverable");
            var hasUnspent = statusFilters.Contains("unspent");

            // Determine recoverable filtering based on UI selection
            if (hasRecoverable && !hasUnspent)
            {
                filterRecoverableOnly = true;
            }
            else if (hasUnspent && !hasRecoverable)
            {
                filterNonRecoverableOnly = true;
            }
            // If both or neither, show all (no recoverable filter)

            // Check for spendable filter
            var hasSpendable = statusFilters.Contains("spendable");
            var hasNonSpendable = statusFilters.Contains("non-spendable");

            if (hasSpendable && hasNonSpendable)
            {
                // Both selected = show all (no filter)
                spendableFilter = null;
            }
            else if (hasSpendable)
            {
                spendableFilter = true;
            }
            else if (hasNonSpendable)
            {
                spendableFilter = false;
            }
        }
        else
        {
            // Default: show unspent and recoverable
            searchTerm = "status:unspent,status:recoverable";
            search = new SearchString(searchTerm);
        }

        // Get contract scripts for the wallet and fetch VTXOs
        var allContracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], cancellationToken: HttpContext.RequestAborted);
        var vtxoContractScripts = allContracts.Select(c => c.Script).ToList();
        var vtxos = await vtxoStorage.GetVtxos(
            scripts: vtxoContractScripts,
            walletIds: [config.WalletId],
            includeSpent: includeSpent,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Apply recoverable filter in-memory if needed
        if (filterRecoverableOnly)
        {
            vtxos = vtxos.Where(v => v.Swept).ToList();
        }
        else if (filterNonRecoverableOnly)
        {
            vtxos = vtxos.Where(v => !v.Swept).ToList();
        }

        // Get spendable coins to determine which VTXOs are actually spendable
        var spendableCoins = await arkadeSpender.GetAvailableCoins(config.WalletId, HttpContext.RequestAborted);
        var spendableOutpoints = spendableCoins
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        // Apply spendable filter if specified
        if (spendableFilter.HasValue)
        {
            vtxos = vtxos
                .Where(vtxo =>
                {
                    var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                    var isSpendable = spendableOutpoints.Contains(outpoint);
                    return spendableFilter.Value ? isSpendable : !isSpendable;
                })
                .ToList();
        }

        // Get contract info for all VTXO scripts
        var vtxoScripts = vtxos.Select(v => v.Script).Distinct().ToArray();
        var vtxoContractsQuery = await contractStorage.GetContracts(
            walletIds: [config.WalletId],
            scripts: vtxoScripts,
            cancellationToken: HttpContext.RequestAborted);
        var vtxoContracts = vtxoContractsQuery.ToDictionary(c => c.Script);

        var model = new StoreVtxosViewModel
        {
            StoreId = storeId,
            Vtxos = vtxos,
            SpendableOutpoints = spendableOutpoints,
            VtxoContracts = vtxoContracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            SearchTerm = searchTerm,
            Search = search
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/intents")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Intents(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Get state filter using helper
        var stateFilter = ParseEnumFilter<ArkIntentState>(searchTerm, "state", s => s switch
        {
            "waiting-submit" => ArkIntentState.WaitingToSubmit,
            "waiting-batch" => ArkIntentState.WaitingForBatch,
            "batch-succeeded" => ArkIntentState.BatchSucceeded,
            "batch-failed" => ArkIntentState.BatchFailed,
            "cancelled" => ArkIntentState.Cancelled,
            _ => null
        });

        var intents = await intentStorage.GetIntents(
            walletIds: [config!.WalletId!],
            states: stateFilter != null ? [stateFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs referenced by intents so the view can show them
        var intentVtxoOutpoints = new Dictionary<string, OutPoint[]>();
        if (intents.Any())
        {
            foreach (var intent in intents)
            {
                if (intent.IntentVtxos.Length > 0)
                    intentVtxoOutpoints[intent.IntentTxId] = intent.IntentVtxos;
            }
        }

        // Fetch full VTXO data for all referenced outpoints
        var allOutpoints = intentVtxoOutpoints.Values.SelectMany(ops => ops).Distinct().ToArray();
        var vtxoLookup = new Dictionary<OutPoint, ArkVtxo>();
        if (allOutpoints.Length > 0)
        {
            var vtxos = await vtxoStorage.GetVtxos(outpoints: allOutpoints, includeSpent: true, cancellationToken: HttpContext.RequestAborted);
            vtxoLookup = vtxos.ToDictionary(v => v.OutPoint);
        }

        return View(new StoreIntentsViewModel
        {
            StoreId = storeId,
            Intents = intents,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            IntentVtxoOutpoints = intentVtxoOutpoints,
            VtxoLookup = vtxoLookup
        });
    }

    [HttpPost("stores/{storeId}/enable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EnableLightning(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var lightningPaymentMethodId = GetLightningPaymentMethod();
        var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

        store!.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], new LightningPaymentMethodConfig
        {
            ConnectionString = await walletOwnership.CreateLightningConnectionString(config!.WalletId!, HttpContext.RequestAborted),
        });
        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lnurlPaymentMethodId], new LNURLPaymentMethodConfig
        {
            UseBech32Scheme = true,
            LUD12Enabled = false
        });

        var blob = store.GetStoreBlob();
        blob.SetExcluded(lightningPaymentMethodId, false);
        blob.OnChainWithLnInvoiceFallback = true;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return RedirectWithSuccess(nameof(Settings), "Lightning enabled", new { storeId });
    }

    [HttpPost("stores/{storeId}/disable-ln")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DisableLightning(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        store!.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);
        await storeRepository.UpdateStore(store);
        return RedirectWithSuccess(nameof(Settings), "Lightning disabled", new { storeId });
    }

    [HttpPost("stores/{storeId}/clear-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ClearWallet(string storeId)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var walletId = config!.WalletId;

        var lnConfig = store!.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled = lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;

        store.SetPaymentMethodConfig(ArkadePlugin.ArkadePaymentMethodId, null);
        if (lnEnabled)
            store.SetPaymentMethodConfig(GetLightningPaymentMethod(), null);

        await storeRepository.UpdateStore(store);

        // Delete wallet from DB if no other store references it
        // Exclude current store since we just cleared its config above (GetStores may return cached data)
        if (!string.IsNullOrEmpty(walletId) && !await walletOwnership.IsWalletUsedByAnyStore(walletId, excludeStoreId: storeId))
        {
            await walletStorage.DeleteWallet(walletId, HttpContext.RequestAborted);
        }

        return RedirectWithSuccess(nameof(GettingStarted), "Arkade wallet settings cleared.", new { storeId });
    }

    [HttpPost("stores/{storeId}/cancel-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> CancelIntent(string storeId, string intentTxId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            // Get the intent from storage - filter by wallet to prevent cross-wallet access
            var intents = await intentStorage.GetIntents(
                walletIds: [config!.WalletId],
                intentTxIds: [intentTxId],
                cancellationToken: cancellationToken);
            var intent = intents.FirstOrDefault();
            if (intent == null)
                return RedirectWithError(nameof(Intents), "Intent not found.", new { storeId });

            // If intent was submitted, delete from server
            if (intent.State == ArkIntentState.WaitingForBatch)
            {
                try
                {
                    await clientTransport.DeleteIntent(intent, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Failed to delete Arkade intent {IntentTxId} from operator while cancelling",
                        intent.IntentTxId);
                }
            }

            // Update storage to mark as cancelled
            await intentStorage.SaveIntent(intent.WalletId, intent with
            {
                State = NArk.Abstractions.Intents.ArkIntentState.Cancelled,
                CancellationReason = "User requested cancellation",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return RedirectWithSuccess(nameof(Intents), "Intent cancelled successfully.", new { storeId });
        }
        catch (InvalidOperationException ex)
        {
            return RedirectWithError(nameof(Intents), ex.Message, new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cancel intent {IntentTxId}", intentTxId);
            return RedirectWithError(nameof(Intents), DescribeArkError(ex, "Failed to cancel intent"), new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/sync-wallet")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncWallet(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            await walletService.SyncWallet(config!.WalletId!, cancellationToken);
            return RedirectWithSuccess(nameof(StoreOverview), "Wallet synchronized successfully. All contracts and VTXOs have been updated.", new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync wallet for store {StoreId}", storeId);
            return RedirectWithError(nameof(StoreOverview), DescribeArkError(ex, "Failed to sync wallet"), new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/sync-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            await vtxoSyncService.PollScriptsForVtxos(contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract VTXOs updated successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync contract for store {StoreId}", storeId);
            return RedirectWithError(nameof(Contracts), DescribeArkError(ex, "Failed to sync contract"), new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/delete-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DeleteContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            // Check if contract has any pending swaps
            var swaps = await swapStorage.GetSwaps(walletIds: [config.WalletId!], contractScripts: [script], status: [ArkSwapStatus.Pending], cancellationToken: cancellationToken);
            if (swaps.Any())
                return RedirectWithError(nameof(Contracts), "Cannot delete contract: It has pending swaps.", new { storeId });

            // Delete the contract (cascade will delete related swaps)
            await contractStorage.DeleteContract(config.WalletId, script, cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract deleted successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete contract for store {StoreId}", storeId);
            return RedirectWithError(nameof(Contracts), DescribeArkError(ex, "Failed to delete contract"), new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/import-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ImportContract(string storeId, string contractString, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (string.IsNullOrWhiteSpace(contractString))
            return RedirectWithError(nameof(Contracts), "Contract string is required.", new { storeId });

        try
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

            // Parse the contract string to validate it
            var arkContract = ArkContractParser.Parse(contractString, terms.Network);
            if (arkContract == null)
                return RedirectWithError(nameof(Contracts), "Failed to parse contract. Invalid contract type or data.", new { storeId });

            var script = arkContract.GetArkAddress().ScriptPubKey;
            var scriptHex = script.ToHex();

            // Check if contract already exists
            var existingContracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], scripts: [scriptHex], cancellationToken: cancellationToken);
            if (existingContracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract already exists in this wallet.", new { storeId });

            // Create the contract using ToEntity and save via storage
            var contractEntity = arkContract.ToEntity(config.WalletId);
            await contractStorage.SaveContract(contractEntity, cancellationToken);

            // Sync the wallet to detect any VTXOs for this contract
            var allContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(allContracts.Select(c => c.Script).ToHashSet(), cancellationToken);

            return RedirectWithSuccess(nameof(Contracts), $"Contract imported successfully: {arkContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet)}", new { storeId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to import contract for store {StoreId}", storeId);
            return RedirectWithError(nameof(Contracts), DescribeArkError(ex, "Failed to import contract"), new { storeId });
        }
    }

    private bool IsArkadeLightningEnabled()
    {
        var store = HttpContext.GetStoreData();
        var lnConfig =
            store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(GetLightningPaymentMethod(), paymentMethodHandlerDictionary);
        var lnEnabled =
            lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        return lnEnabled;
    }

    private static TemporaryWalletSettings GetFromInputWallet(string? wallet)
    {
        if (string.IsNullOrWhiteSpace(wallet))
            return new TemporaryWalletSettings(GenerateWallet(), true);

        // Check if input is a BIP-39 mnemonic (12 or 24 words)
        var words = wallet.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 12 or 24)
        {
            try
            {
                // Validate the mnemonic
                var mnemonic = new Mnemonic(wallet.Trim(), Wordlist.English);
                return new TemporaryWalletSettings(mnemonic.ToString(), false);
            }
            catch
            {
                // Not a valid mnemonic, fall through to the error below
            }
        }

        throw new Exception("Unsupported value. Enter a BIP-39 seed phrase (12 or 24 words).");
    }
    private static string GenerateWallet()
    {
        // Generate HD wallet with BIP-39 mnemonic (12 words)
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        return mnemonic.ToString();
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

    private record TemporaryWalletSettings(string Wallet, bool IsNewlyGeneratedWallet);

    [HttpGet("~/stores/{storeId}/payout-processors/ark-automated")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId)
    {
        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery
                {
                    Stores = [storeId],
                    Processors = [payoutSenderFactory.Processor],
                    PayoutMethods = [ArkadePlugin.ArkadePayoutMethodId]
                }))
            .FirstOrDefault();

        var blob = activeProcessor is null
            ? new ArkAutomatedPayoutBlob()
            : ArkAutomatedPayoutProcessor.GetBlob(activeProcessor);
        return View(new ConfigureArkPayoutProcessorViewModel(blob));
    }

    [HttpPost("~/stores/{storeId}/payout-processors/ark-automated")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ConfigurePayoutProcessor(string storeId, ConfigureArkPayoutProcessorViewModel automatedTransferBlob)
    {
        if (!ModelState.IsValid)
            return View(automatedTransferBlob);

        var activeProcessor =
            (await payoutProcessorService.GetProcessors(
                new PayoutProcessorService.PayoutProcessorQuery
                {
                    Stores = [storeId],
                    Processors = [payoutSenderFactory.Processor],
                    PayoutMethods = [ArkadePlugin.ArkadePayoutMethodId]
                }))
            .FirstOrDefault();

        activeProcessor ??= new PayoutProcessorData();
        activeProcessor.HasTypedBlob<ArkAutomatedPayoutBlob>().SetBlob(automatedTransferBlob.ToBlob());
        activeProcessor.StoreId = storeId;
        activeProcessor.PayoutMethodId = ArkadePlugin.ArkadePayoutMethodId.ToString();
        activeProcessor.Processor = payoutSenderFactory.Processor;

        var tcs = new TaskCompletionSource();
        eventAggregator.Publish(new PayoutProcessorUpdated
        {
            Data = activeProcessor,
            Id = activeProcessor.Id,
            Processed = tcs
        });
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Processor updated."
        });
        await tcs.Task;

        return RedirectToAction(nameof(ConfigurePayoutProcessor), "Ark", new { storeId });
    }

    [HttpGet("blockchain-info")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBlockchainInfo(CancellationToken cancellationToken = default)
    {
        try
        {
            var (timestamp, height) = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
            return Json(new { timestamp, height });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch blockchain info");
            return StatusCode(500, new { error = "Failed to fetch blockchain info" });
        }
    }

    [HttpGet("~/ark-admin/wallet/{walletId}")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminWalletOverview(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        // Check if wallet exists
        var adminWallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        if (adminWallet == null)
            return RedirectWithError(nameof(ListWallets), "Wallet not found.");

        var balances = await walletService.GetBalances(walletId, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken) != null;

        // Check Ark Operator connection using helper
        var (arkOperatorConnected, arkOperatorError) = await CheckServiceConnectionAsync(
            ct => clientTransport.GetServerInfoAsync(ct), cancellationToken);

        // Check Boltz connection using helper
        var (boltzConnected, boltzError) = boltzClient != null
            ? await CheckServiceConnectionAsync(ct => boltzClient.GetVersionAsync(), cancellationToken)
            : (false, null);

        ViewData["IsAdminView"] = true;
        ViewData["WalletId"] = walletId;

        return View("StoreOverview", new StoreOverviewViewModel
        {
            IsLightningEnabled = false, // Admin view doesn't check Lightning
            Balances = balances,
            WalletId = walletId,
            SignerAvailable = signerAvailable,
            HasSecret = !string.IsNullOrEmpty(adminWallet.Secret),
            ArkOperatorUrl = arkNetworkConfig.ArkUri,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = ArkOperatorAvailability.DescribeMessage(arkOperatorError),
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError
        });
    }

    [HttpGet("~/ark-admin/wallets")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ListWallets(CancellationToken cancellationToken)
    {
        var wallets = await GetWalletsWithDetailsAsync(cancellationToken);
        return View(wallets);
    }

    [HttpPost("~/ark-admin/wallet/{walletId}/delete")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminDeleteWallet(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        try
        {
            // Check if wallet exists
            var wallet = await GetWalletWithDetailsAsync(walletId, cancellationToken);
            if (wallet == null)
                return RedirectWithError(nameof(ListWallets), "Wallet not found.");

            // Check if wallet has any pending swaps
            var hasPendingSwaps = await HasPendingSwapsAsync(walletId, cancellationToken);
            if (hasPendingSwaps)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending swaps.", new { walletId });

            // Check if wallet has any pending intents
            var hasPendingIntents = await HasPendingIntentsAsync(walletId, cancellationToken);
            if (hasPendingIntents)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending intents.", new { walletId });

            // Delete the wallet and all associated data
            await walletStorage.DeleteWallet(walletId, cancellationToken);
            return RedirectWithSuccess(nameof(ListWallets), $"Wallet {walletId} and all associated data deleted successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete wallet {WalletId}", walletId);
            return RedirectWithError(nameof(AdminWalletOverview), DescribeArkError(ex, "Failed to delete wallet"), new { walletId });
        }
    }

    #region Helper Methods

    /// <summary>
    /// Validates store data and Arkade configuration, returning an error result if validation fails.
    /// </summary>
    private (StoreData? store, ArkadePaymentMethodConfig? config, IActionResult? errorResult)
        ValidateStoreAndConfig()
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return (null, null, NotFound());

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return (null, null, RedirectToAction(nameof(GettingStarted), new { storeId = store.Id }));

        return (store, config, null);
    }

    /// <summary>
    /// Redirects to an action with a success message.
    /// </summary>
    private IActionResult RedirectWithSuccess(string action, string message, object? routeValues = null)
    {
        TempData[WellKnownTempData.SuccessMessage] = message;
        return RedirectToAction(action, routeValues);
    }

    /// <summary>
    /// Redirects to an action with an error message.
    /// </summary>
    private IActionResult RedirectWithError(string action, string message, object? routeValues = null)
    {
        TempData[WellKnownTempData.ErrorMessage] = message;
        return RedirectToAction(action, routeValues);
    }

    /// <summary>
    /// Maps an exception to a user-facing message. When the Arkade operator is unreachable
    /// it returns the friendly <see cref="ArkOperatorAvailability.UnavailableMessage"/> and
    /// flips the status banner immediately (so the next page already reflects the outage);
    /// otherwise it returns the original error prefixed with <paramref name="context"/>.
    /// </summary>
    private string DescribeArkError(Exception ex, string context)
    {
        arkOperatorHealth.ReportFailure(ex); // no-op unless ex looks like operator-unreachable
        return ArkOperatorAvailability.Describe(ex, context);
    }

    /// <summary>
    /// Checks service connection and returns connection status.
    /// </summary>
    private async Task<(bool connected, string? error)> CheckServiceConnectionAsync<T>(
        Func<CancellationToken, Task<T?>> connectionTest,
        CancellationToken ct)
    {
        try
        {
            var result = await connectionTest(ct);
            return (result != null, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Parses an enum filter from search term.
    /// </summary>
    private T? ParseEnumFilter<T>(string? searchTerm, string filterName, Func<string, T?> mapper) where T : struct
    {
        var search = new SearchString(searchTerm);
        if (!search.ContainsFilter(filterName)) return null;
        var filters = search.GetFilterArray(filterName);
        return filters.Length == 1 ? mapper(filters[0]) : null;
    }

    /// <summary>
    /// Parses a boolean filter from search term.
    /// </summary>
    private bool? ParseBooleanFilter(string? searchTerm, string filterName, string trueValue)
    {
        var search = new SearchString(searchTerm);
        if (!search.ContainsFilter(filterName)) return null;
        var filters = search.GetFilterArray(filterName);
        return filters.Length == 1 ? filters[0] == trueValue : null;
    }

    /// <summary>
    /// Gets Boltz connection status and cached limits.
    /// </summary>
    private async Task<(bool connected, string? error, BoltzAllLimits? limits)> GetBoltzConnectionStatusAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arkNetworkConfig.BoltzUri))
            return (false, null, null);

        var status = await boltzHealth.GetStatusAsync(cancellationToken);
        if (!status.Available)
            return (false, status.Error, null);

        if (boltzLimitsValidator == null)
            return (false, null, null);

        try
        {
            var limits = await boltzLimitsValidator.GetAllLimitsAsync(cancellationToken);
            return (limits != null, limits == null ? "Boltz instance does not support Arkade" : null, limits);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to fetch Boltz limits");
            return (false, BoltzHealthService.UnavailableMessage, null);
        }
    }

    #endregion

    #region Mass Actions

    [HttpPost("stores/{storeId}/vtxos/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionVtxos(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Vtxos), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "refresh-state":
                    // Look up selected VTXOs to get their scripts, then resolve contracts
                    var outpoints = selectedItems
                        .Select(s => NBitcoin.OutPoint.Parse(s.Replace('-', ':')))
                        .ToArray();
                    var selectedVtxos = await vtxoStorage.GetVtxos(
                        outpoints: outpoints, includeSpent: true, cancellationToken: cancellationToken);
                    var vtxoScripts = selectedVtxos.Select(v => v.Script).Distinct().ToArray();
                    var boardingContracts = await contractStorage.GetContracts(
                        scripts: vtxoScripts, scope: ContractScope.Onchain, cancellationToken: cancellationToken);
                    var nonBoardingScripts = (await contractStorage.GetContracts(
                            scripts: vtxoScripts, scope: ContractScope.Offchain, cancellationToken: cancellationToken))
                        .Select(c => c.Script).ToHashSet();
                    if (nonBoardingScripts.Count > 0)
                        await vtxoSyncService.PollScriptsForVtxos(nonBoardingScripts, cancellationToken);
                    if (boardingContracts.Count > 0)
                        await boardingUtxoSyncService.SyncAsync(boardingContracts, cancellationToken);
                    return RedirectWithSuccess(nameof(Vtxos), $"Refreshed state for {selectedItems.Length} VTXOs.", new { storeId });

                default:
                    return RedirectWithError(nameof(Vtxos), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VTXO mass action {Command} failed for store {StoreId}", command, storeId);
            return RedirectWithError(nameof(Vtxos), DescribeArkError(ex, "Mass action failed"), new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/swaps/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionSwaps(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Swaps), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "poll-status":
                    if (boltzClient == null)
                        return RedirectWithError(nameof(Swaps), "Boltz client is not configured.", new { storeId });

                    var updatedCount = 0;
                    // Batch fetch all swaps at once for efficiency
                    var swapsToCheck = await swapStorage.GetSwaps(
                        walletIds: [config!.WalletId!],
                        swapIds: selectedItems,
                        cancellationToken: cancellationToken);
                    var swapsDict = swapsToCheck.ToDictionary(s => s.SwapId);

                    foreach (var swapId in selectedItems)
                    {
                        if (!swapsDict.TryGetValue(swapId, out var swap))
                            continue;

                        var statusResponse = await boltzClient.GetSwapStatusAsync(swapId, cancellationToken);
                        var newStatus = MapBoltzStatus(statusResponse.Status, swap.Status);

                        if (swap.Status != newStatus)
                        {
                            await swapStorage.UpdateSwapStatus(config.WalletId!, swapId, newStatus, cancellationToken: cancellationToken);
                            updatedCount++;
                        }
                    }
                    return RedirectWithSuccess(nameof(Swaps), $"Polled {selectedItems.Length} swaps. {updatedCount} status updates.", new { storeId });

                default:
                    return RedirectWithError(nameof(Swaps), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Swap mass action {Command} failed for store {StoreId}", command, storeId);
            return RedirectWithError(nameof(Swaps), "Mass action failed. Check the Boltz connection and try again.", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionContracts(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "sync-selected":
                    // Poll scripts for VTXO updates, routing boarding contracts to UTXO provider
                    var selectedBoarding = await contractStorage.GetContracts(
                        scripts: selectedItems, scope: ContractScope.Onchain, cancellationToken: cancellationToken);
                    var selectedNonBoardingScripts = (await contractStorage.GetContracts(
                            scripts: selectedItems, scope: ContractScope.Offchain, cancellationToken: cancellationToken))
                        .Select(c => c.Script).ToHashSet();
                    if (selectedNonBoardingScripts.Count > 0)
                        await vtxoSyncService.PollScriptsForVtxos(selectedNonBoardingScripts, cancellationToken);
                    if (selectedBoarding.Count > 0)
                        await boardingUtxoSyncService.SyncAsync(selectedBoarding, cancellationToken);
                    return RedirectWithSuccess(nameof(Contracts), $"Synced {selectedItems.Length} contracts.", new { storeId });

                case "set-active":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Active, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Active.", new { storeId });

                case "set-inactive":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Inactive, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Inactive.", new { storeId });

                case "set-awaiting":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Awaiting Funds.", new { storeId });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Contract mass action {Command} failed for store {StoreId}", command, storeId);
            return RedirectWithError(nameof(Contracts), DescribeArkError(ex, "Mass action failed"), new { storeId });
        }
    }

    #endregion

    #region Send helpers - destination parsing, LNURL resolution, payouts

    /// <summary>
    /// Runs a send that may fulfill payouts. When any of <paramref name="outputs"/> is
    /// payout-backed, the payouts are claimed under the fulfillment lock <b>before</b> the
    /// spend executes (see <see cref="ArkPayoutFulfillmentService"/>) and the returned error
    /// message — instead of a spend — signals a payout that could not be claimed.
    /// </summary>
    private async Task<string?> SpendFulfillingPayouts(
        IEnumerable<SendOutputViewModel> outputs,
        Func<CancellationToken, Task<SpendResult>> spend,
        CancellationToken token)
    {
        var payoutIds = outputs
            .Where(o => !string.IsNullOrEmpty(o.PayoutId))
            .Select(o => o.PayoutId!)
            .ToArray();

        if (payoutIds.Length == 0)
        {
            await spend(token);
            return null;
        }

        var fulfillment = await payoutFulfillment.FulfillPayouts(payoutIds, spend, token);
        return fulfillment.Executed ? null : fulfillment.Error;
    }

    private static DestinationType? MapSendTypeToDestinationType(SendDestinationType type) => type switch
    {
        SendDestinationType.ArkAddress => DestinationType.ArkAddress,
        SendDestinationType.Bip21Ark => DestinationType.Bip21Uri,
        SendDestinationType.Bip21Lightning => DestinationType.Bip21Uri,
        SendDestinationType.LightningInvoice => DestinationType.LightningInvoice,
        SendDestinationType.Lnurl => DestinationType.LnurlPay,
        _ => null
    };

    private async Task<(LNURLPayRequest? info, string? error)> ResolveLnurlAsync(
        string destination, CancellationToken token)
    {
        Uri lnurl;
        if (destination.IsValidEmail())
            lnurl = LNURL.LNURL.ExtractUriFromInternetIdentifier(destination);
        else
            lnurl = LNURL.LNURL.Parse(destination, out _);

        var httpClient = httpClientFactory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);

        var rawInfo = await LNURL.LNURL.FetchInformation(lnurl, httpClient, linked.Token);
        if (rawInfo is not LNURLPayRequest info)
            return (null, "Not a valid LNURL-pay endpoint");

        return (info, null);
    }

    private async Task<(string? bolt11, string? error)> ResolveLnurlToInvoiceAsync(
        string destination, long amountSats, Network network, CancellationToken token)
    {
        var (info, error) = await ResolveLnurlAsync(destination, token);
        if (info == null) return (null, error ?? "LNURL resolution failed");

        var lm = new LightMoney(amountSats, LightMoneyUnit.Satoshi);
        if (lm < info.MinSendable || lm > info.MaxSendable)
            return (null, $"Amount {amountSats} sats outside LNURL range ({info.MinSendable.ToUnit(LightMoneyUnit.Satoshi)}-{info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi)} sats)");

        var httpClient = httpClientFactory.CreateClient();
        var callback = await info.SendRequest(lm, network, httpClient, cancellationToken: token);
        var bolt11 = callback.GetPaymentRequest(network);
        return (bolt11.ToString(), null);
    }

    private async Task<SendDestinationViewModel> ParseSendDestinationAsync(
        string rawDestination, decimal? amountBtc, Network network, CancellationToken token)
    {
        // Check if it's an LNURL or Lightning Address FIRST
        if (rawDestination.IsValidEmail() ||
            rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
        {
            var result = new SendDestinationViewModel { RawDestination = rawDestination };
            try
            {
                var (info, lnurlError) = await ResolveLnurlAsync(rawDestination, token);
                if (info == null)
                {
                    result.Type = SendDestinationType.Lnurl;
                    result.Error = lnurlError;
                    return result;
                }

                result.Type = SendDestinationType.Lnurl;
                result.ResolvedAddress = rawDestination;
                result.LnurlMinSats = (long)info.MinSendable.ToUnit(LightMoneyUnit.Satoshi);
                result.LnurlMaxSats = (long)info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi);

                // Intersect with Boltz submarine swap limits
                if (boltzLimitsValidator != null)
                {
                    var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                    if (limits != null)
                    {
                        result.LnurlMinSats = Math.Max(result.LnurlMinSats, limits.SubmarineMinAmount);
                        result.LnurlMaxSats = Math.Min(result.LnurlMaxSats, limits.SubmarineMaxAmount);
                    }
                }

                result.AmountSats = amountBtc is { } amount ? Money.Coins(amount).Satoshi : 0L;
                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Type = SendDestinationType.Lnurl;
                result.Error = $"LNURL resolution failed: {ex.Message}";
                return result;
            }
        }

        // Delegate to existing sync method for all other types
        return ParseSendDestination(rawDestination, amountBtc, network);
    }

    private static bool IsLightningDestination(string dest) => ArkSpendHelpers.IsLightningDestination(dest);

    private static SendDestinationViewModel ParseSendDestination(string rawDestination, decimal? amountBtc, Network network)
    {
        var parsed = ArkSpendHelpers.ParseSendDestination(rawDestination, amountBtc, network);
        return new SendDestinationViewModel
        {
            RawDestination = parsed.RawDestination,
            Type = parsed.Type,
            ResolvedAddress = parsed.ResolvedAddress,
            AmountSats = parsed.AmountSats,
            PayoutId = parsed.PayoutId,
            LnurlMinSats = parsed.LnurlMinSats,
            LnurlMaxSats = parsed.LnurlMaxSats,
            IsValid = parsed.IsValid,
            Error = parsed.Error
        };
    }

    /// <summary>
    /// Parses the destinations query parameter which can contain:
    /// - Full BIP21 URIs (comma-separated, may contain colons in scheme)
    /// - Simple format: addr:amount pairs (comma-separated)
    /// </summary>
    private List<SendDestinationViewModel> ParseDestinationsParam(string destinations, Network network)
    {
        var result = new List<SendDestinationViewModel>();

        // Smart split: don't split on commas inside BIP21 URIs
        // BIP21 URIs start with "bitcoin:" and may contain query params with commas
        var parts = new List<string>();
        var currentPart = "";
        var inUri = false;

        foreach (var c in destinations)
        {
            if (c == 'b' && currentPart == "" && destinations.IndexOf("bitcoin:", destinations.IndexOf(c.ToString()), StringComparison.OrdinalIgnoreCase) == destinations.IndexOf(c.ToString()))
            {
                inUri = true;
            }

            if (c == ',' && !inUri)
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                continue;
            }

            // End of URI detection (space or next bitcoin:)
            if (inUri && (c == ' ' || (c == ',' && currentPart.Contains('?'))))
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                inUri = c != ',';
                continue;
            }

            currentPart += c;
        }

        if (!string.IsNullOrWhiteSpace(currentPart))
            parts.Add(currentPart.Trim());

        foreach (var part in parts)
        {
            // Check if this is a BIP21 URI
            if (part.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSendDestination(part, null, network);
                result.Add(parsed);
            }
            // Check if this is a Lightning invoice
            else if (part.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
                     part.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSendDestination(part, null, network);
                result.Add(parsed);
            }
            // Check if this is an Ark address (no colon, or ark1 prefix)
            else if (part.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
                     ArkAddress.TryParse(part.Split(':')[0], out _))
            {
                // Could be addr:amount format
                var segments = part.Split(':', 2);
                var rawDest = segments[0].Trim();
                decimal? amount = segments.Length > 1 &&
                                  decimal.TryParse(segments[1], System.Globalization.CultureInfo.InvariantCulture, out var amt)
                    ? amt
                    : null;

                var parsed = ParseSendDestination(rawDest, amount, network);
                result.Add(parsed);
            }
            else
            {
                // Unknown format, try to parse anyway
                var parsed = ParseSendDestination(part, null, network);
                result.Add(parsed);
            }
        }

        return result;
    }

    #endregion

    #region BTCPay-specific wallet storage helpers

    /// <summary>
    /// BTCPay-specific helper to get all wallets with their related contracts and swaps.
    /// </summary>
    private async Task<List<ArkWalletEntity>> GetWalletsWithDetailsAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to get a wallet with its related contracts and swaps.
    /// </summary>
    private async Task<ArkWalletEntity?> GetWalletWithDetailsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to check if a wallet has pending swaps.
    /// </summary>
    private async Task<bool> HasPendingSwapsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Swaps
            .AnyAsync(s => s.WalletId == walletId &&
                          s.Status == ArkSwapStatus.Pending,
                     cancellationToken);
    }

    /// <summary>
    /// BTCPay-specific helper to check if a wallet has pending intents.
    /// </summary>
    private async Task<bool> HasPendingIntentsAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await ctx.Intents
            .AnyAsync(i => i.WalletId == walletId &&
                          (i.State == ArkIntentState.WaitingToSubmit ||
                           i.State == ArkIntentState.WaitingForBatch ||
                           i.State == ArkIntentState.BatchInProgress),
                     cancellationToken);
    }

    #endregion
}
