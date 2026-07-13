using System.Threading.Channels;
using BTCPayServer;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Core.Transport;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Swaps.Services;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkContractInvoiceListener(
    IMemoryCache memoryCache,
    InvoiceRepository invoiceRepository,
    ArkadePaymentMethodHandler arkadePaymentMethodHandler,
    IClientTransport clientTransport,
    EventAggregator eventAggregator,
    IContractStorage contractStorage,
    PaymentService paymentService,
    IVtxoStorage vtxoStorage,
    ISwapStorage swapStorage,
    ILogger<ArkContractInvoiceListener> logger)
    : IHostedService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(2);

    private readonly Channel<string> _checkInvoices = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _paymentLock = new(1, 1);
    private CompositeDisposable _leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await QueueMonitoredInvoices(cancellationToken);
        _leases.Add(eventAggregator.SubscribeAsync<InvoiceEvent>(OnInvoiceEvent));

        // Subscribe to NNark's storage events directly
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        swapStorage.SwapsChanged += OnSwapChanged;


        _ = PollAllInvoices(cancellationToken);
        _ = ReconcilePaymentsLoop(cancellationToken);
    }

    private async void OnSwapChanged(object? sender, NArk.Swaps.Models.ArkSwap swap)
    {
        try
        {
            // Only process reverse submarine swaps (Lightning -> Ark)
            if (swap.SwapType != NArk.Swaps.Models.ArkSwapType.ReverseSubmarine)
                return;

            var activityState = swap.Status == NArk.Swaps.Models.ArkSwapStatus.Pending
                ? ContractActivityState.Active
                : ContractActivityState.Inactive;
            await contractStorage.UpdateContractActivityState(swap.WalletId, swap.ContractScript, activityState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling swap change for {SwapId}", swap.SwapId);
        }
    }

    private async Task OnInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        memoryCache.Remove(GetCacheKey(invoiceEvent.Invoice.Id));
        _checkInvoices.Writer.TryWrite(invoiceEvent.Invoice.Id);
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            // Note: Auto-deactivation of AwaitingFundsBeforeDeactivate contracts is now handled
            // by VtxoSynchronizationService in NNark library

            var terms = await clientTransport.GetServerInfoAsync();
            var network = terms.Network;
            var script = Script.FromHex(vtxo.Script);

            var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
            var address = ArkAddress.FromScriptPubKey(script, serverKey);
            var paymentDestination = address.ToString(network.ChainName == ChainName.Mainnet);
            var inv = await invoiceRepository.GetInvoiceFromAddress(
                ArkadePlugin.ArkadePaymentMethodId, paymentDestination);

            if (inv is null)
                return;

            // Map NNark's ArkVtxo to plugin's VtxoEntity entity
            var vtxoEntity = new VtxoEntity
            {
                TransactionId = vtxo.TransactionId,
                TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                Script = vtxo.Script,
                SeenAt = vtxo.CreatedAt
            };
            await HandlePaymentData(vtxoEntity, inv, arkadePaymentMethodHandler);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling VTXO change for {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation("Invoice {invoiceId} received payment {amount} {currency} {paymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }
    
    private async Task HandlePaymentData(VtxoEntity vtxo, InvoiceEntity invoice, ArkadePaymentMethodHandler handler)
    {
        var pmi = ArkadePlugin.ArkadePaymentMethodId;
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        const PaymentStatus status = PaymentStatus.Settled;

        // Serialize payment registration to prevent duplicate inserts from concurrent VTXO events
        await _paymentLock.WaitAsync();
        try
        {
            // Re-fetch the invoice inside the lock to get the latest payment state
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null)
                return;

            var paymentData = new PaymentData
            {
                Status = status,
                Amount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
                Created = vtxo.SeenAt,
                Id = details.Outpoint,
                Currency = "BTC",
            }.Set(freshInvoice, handler, details);

            var alreadyExistingPaymentThatMatches = freshInvoice
                .GetPayments(false)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (alreadyExistingPaymentThatMatches == null)
            {
                var payment = await paymentService.AddPayment(paymentData);
                if (payment != null)
                {
                    await ReceivedPayment(freshInvoice, payment);
                }
            }
            else
            {
                // Update existing payment if details changed
                alreadyExistingPaymentThatMatches.Status = status;
                alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
                await paymentService.UpdatePayments([alreadyExistingPaymentThatMatches]);
            }
        }
        finally
        {
            _paymentLock.Release();
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }
    
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        swapStorage.SwapsChanged -= OnSwapChanged;
        _leases.Dispose();
        _leases = new CompositeDisposable();
    }

    public async Task ToggleArkadeContract(InvoiceEntity invoice)
    {
        var activityState = IsMonitored(invoice)
            ? ContractActivityState.Active
            : ContractActivityState.Inactive;
        var listenedContract = GetListenedArkadeInvoice(invoice);
        if (listenedContract is null)
        {
            return;
        }

        // ConfigurePrompt tags the Payment contract with Source = "invoice:{id}".
        // Find every contract carrying this invoice's source tag and toggle them all.
        // HTLC contracts use a different "swap:{id}" Source tag and are
        // driven by OnSwapChanged based on swap state, not invoice state.
        var walletId = listenedContract.Details.WalletId;
        var invoiceSource = $"invoice:{invoice.Id}";
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            cancellationToken: CancellationToken.None);
        foreach (var c in contracts.Where(c => c.Metadata?.GetValueOrDefault("Source") == invoiceSource))
        {
            await contractStorage.UpdateContractActivityState(walletId, c.Script, activityState);
        }
    }

    // BTCPay keeps watching expired invoices until MonitoringExpiration so a late
    // payment can still mark them PaidLate; keep polling their contracts until then.
    private static bool IsMonitored(InvoiceEntity invoice)
    {
        if (invoice.Status is InvoiceStatus.New or InvoiceStatus.Processing)
            return true;
        return invoice.Status == InvoiceStatus.Expired && invoice.MonitoringExpiration > DateTimeOffset.UtcNow;
    }

    private ArkadeListenedContract? GetListenedArkadeInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        if (prompt?.Details is null)
            return null;

        return new ArkadeListenedContract(
            arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details),
            invoice.Id);
    }

    private static DateTimeOffset GetExpiration(InvoiceEntity invoice)
    {
        var expiredIn = DateTimeOffset.UtcNow - invoice.ExpirationTime;
        return DateTimeOffset.UtcNow + (expiredIn >= TimeSpan.FromMinutes(5.0) ? expiredIn : TimeSpan.FromMinutes(5.0));
    }

    private string GetCacheKey(string invoiceId)
    {
        return $"{nameof(GetListenedArkadeInvoice)}-{invoiceId}";
    }

    private Task<InvoiceEntity> GetInvoice(string invoiceId)
    {
        return memoryCache.GetOrCreateAsync(GetCacheKey(invoiceId), async cacheEntry =>
        {
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return null;
            cacheEntry.AbsoluteExpiration = GetExpiration(invoice);
            return invoice;
        })!;
    }


    private async Task QueueMonitoredInvoices(CancellationToken cancellation)
    {
        foreach (var invoice in await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId,
                     cancellation))
        {
            if (GetListenedArkadeInvoice(invoice) is null) continue;
            _checkInvoices.Writer.TryWrite(invoice.Id);
            memoryCache.Set(GetCacheKey(invoice.Id), invoice, GetExpiration(invoice));
        }

    }

    private async Task PollAllInvoices(CancellationToken cancellation)
    {
        retry:
        if (cancellation.IsCancellationRequested)
            return;
        try
        {
            await foreach (var invoiceId in _checkInvoices.Reader.ReadAllAsync(cancellation))
            {
                logger.LogInformation("Checking for invoice {InvoiceId}", invoiceId);
                var invoice = await GetInvoice(invoiceId);
                await ToggleArkadeContract(invoice);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Task.Delay(1000, cancellation);
            logger.LogWarning(ex, "Unhandled error in the Arkade invoice listener.");
            goto retry;
        }
        
        logger.LogInformation("Exiting poll loop.");
    }

    private async Task ReconcilePaymentsLoop(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await ReconcilePayments(cancellation);
                }
                catch when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error during Arkade payment reconciliation.");
                }

                await Task.Delay(ReconciliationInterval, cancellation);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Re-derives payments from stored VTXOs instead of relying on VtxosChanged: the
    /// storage only raises that event when a row is inserted or updated, so a payment
    /// lost between the VTXO commit and AddPayment (e.g. a crash) is never replayed by
    /// re-polling. Also deactivates invoice contracts whose invoice left the monitored set.
    /// </summary>
    private async Task ReconcilePayments(CancellationToken cancellation)
    {
        var invoices = (await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId, cancellation))
            .ToDictionary(i => i.Id);

        var activeContracts = await contractStorage.GetContracts(isActive: true, cancellationToken: cancellation);
        foreach (var contract in activeContracts)
        {
            if (contract.Metadata?.GetValueOrDefault("Source") is not { } source ||
                !source.StartsWith("invoice:", StringComparison.Ordinal))
                continue;

            var invoiceId = source["invoice:".Length..];
            if (invoices.ContainsKey(invoiceId))
                continue;

            var invoice = await GetInvoice(invoiceId);
            if (invoice is not null && IsMonitored(invoice))
            {
                invoices.TryAdd(invoiceId, invoice);
                continue;
            }

            await contractStorage.UpdateContractActivityState(
                contract.WalletIdentifier, contract.Script, ContractActivityState.Inactive, cancellation);
        }

        if (invoices.Count == 0)
            return;

        var terms = await clientTransport.GetServerInfoAsync(cancellation);
        var invoicesByScript = new Dictionary<string, InvoiceEntity>();
        foreach (var invoice in invoices.Values)
        {
            try
            {
                var contract = GetListenedArkadeInvoice(invoice)?.Details.GetContract(terms.Network);
                if (contract is not null)
                    invoicesByScript.TryAdd(contract.GetArkAddress().ScriptPubKey.ToHex(), invoice);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not derive the Arkade contract script for invoice {InvoiceId}", invoice.Id);
            }
        }

        if (invoicesByScript.Count == 0)
            return;

        var vtxos = await vtxoStorage.GetVtxos(
            scripts: invoicesByScript.Keys.ToArray(),
            includeSpent: true,
            cancellationToken: cancellation);
        foreach (var vtxo in vtxos)
        {
            if (!invoicesByScript.TryGetValue(vtxo.Script, out var invoice))
                continue;

            var outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";
            if (invoice.GetPayments(false).Any(p =>
                    p.Id == outpoint && p.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId))
                continue;

            var vtxoEntity = new VtxoEntity
            {
                TransactionId = vtxo.TransactionId,
                TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                Script = vtxo.Script,
                SeenAt = vtxo.CreatedAt
            };
            await HandlePaymentData(vtxoEntity, invoice, arkadePaymentMethodHandler);
        }
    }
}
