using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Reporting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services;

/// <summary>
/// Extends BTCPay's default Payments report so exports carry the Arkade settlement
/// details: the Boltz swap id and settlement address/transaction for Lightning payments
/// received through a reverse swap, and the funding transaction for Arkade payments.
/// Delegates the report itself to <see cref="PaymentsReportProvider"/> and enriches the
/// produced rows, so upstream changes to the report flow through unchanged.
/// </summary>
public class ArkadePaymentsReportProvider(
    PaymentsReportProvider defaultPaymentsReportProvider,
    InvoiceRepository invoiceRepository,
    PaymentMethodHandlerDictionary handlers,
    ArkadeSettlementDataProvider settlementDataProvider,
    ILogger<ArkadePaymentsReportProvider> logger) : ReportProvider
{
    public override string Name => "Payments";

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        await defaultPaymentsReportProvider.Query(queryContext, cancellation);

        var fields = queryContext.ViewDefinition!.Fields;
        var invoiceIdIndex = IndexOf(fields, "InvoiceId");
        var categoryIndex = IndexOf(fields, "Category");
        var paymentMethodIdIndex = IndexOf(fields, "PaymentMethodId");
        var addressIndex = IndexOf(fields, "Address");
        if (invoiceIdIndex < 0 || paymentMethodIdIndex < 0)
        {
            logger.LogWarning(
                "The default Payments report no longer exposes the InvoiceId/PaymentMethodId fields; skipping Arkade settlement enrichment");
            return;
        }

        // Keep the settlement columns next to LightningAddress, the position they had
        // before this provider delegated to the default one.
        var lightningAddressIndex = IndexOf(fields, "LightningAddress");
        var insertAt = lightningAddressIndex >= 0 ? lightningAddressIndex + 1 : fields.Count;
        fields.Insert(insertAt, new("BoltzSwapId", "string"));
        fields.Insert(insertAt + 1, new("SettlementCurrency", "string"));
        fields.Insert(insertAt + 2, new("SettlementAddress", "string"));
        fields.Insert(insertAt + 3, new("SettlementTransactionId", "string"));
        if (categoryIndex >= insertAt)
            categoryIndex += 4;

        var invoices = (await invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [queryContext.StoreId],
            StartDate = queryContext.From,
            EndDate = queryContext.To,
        }, cancellation)).ToDictionary(invoice => invoice.Id);
        var unmatchedPayments = new Dictionary<string, List<PaymentEntity>>();

        foreach (var row in queryContext.Data)
        {
            var payment = TakeMatchingPayment(
                invoices,
                unmatchedPayments,
                row[invoiceIdIndex] as string,
                row[paymentMethodIdIndex] as string,
                addressIndex >= 0 ? row[addressIndex] as string : null,
                out var invoice);

            ArkadeSettlementData? settlementData = null;
            string? arkadeTransactionId = null;
            if (payment is not null && invoice is not null)
            {
                handlers.TryGetValue(payment.PaymentMethodId, out var handler);
                if (handler is ILightningPaymentHandler)
                {
                    settlementData = await settlementDataProvider.GetSettlementData(invoice, payment, cancellation);
                    if (settlementData is not null && categoryIndex >= 0)
                        row[categoryIndex] = "Lightning via Boltz";
                }
                else if (handler is ArkadePaymentMethodHandler)
                {
                    arkadeTransactionId = TryGetArkadeTransactionId(handler, payment);
                    if (categoryIndex >= 0)
                        row[categoryIndex] = "Arkade";
                }
            }

            row.Insert(insertAt, settlementData?.SwapId);
            row.Insert(insertAt + 1, settlementData?.SettlementCurrency);
            row.Insert(insertAt + 2, settlementData?.SettlementAddress);
            row.Insert(insertAt + 3, settlementData?.SettlementTransactionId ?? arkadeTransactionId);
        }
    }

    private static int IndexOf(IList<StoreReportResponse.Field> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name == name)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Resolves the payment a report row was generated from. The default provider emits
    /// one row per payment of an invoice, so the row's invoice id plus payment method and
    /// destination identify the payment; the per-invoice list consumes matches in order so
    /// duplicate destinations still pair up one-to-one.
    /// </summary>
    private static PaymentEntity? TakeMatchingPayment(
        Dictionary<string, InvoiceEntity> invoices,
        Dictionary<string, List<PaymentEntity>> unmatchedPayments,
        string? invoiceId,
        string? paymentMethodId,
        string? destination,
        out InvoiceEntity? invoice)
    {
        invoice = null;
        if (invoiceId is null || !invoices.TryGetValue(invoiceId, out invoice))
            return null;

        if (!unmatchedPayments.TryGetValue(invoiceId, out var payments))
        {
            payments = invoice.GetPayments(true).ToList();
            unmatchedPayments.Add(invoiceId, payments);
        }

        var match = payments.FirstOrDefault(payment =>
            payment.PaymentMethodId.ToString() == paymentMethodId &&
            payment.Destination == destination);
        if (match is not null)
            payments.Remove(match);
        return match;
    }

    /// <summary>
    /// For direct Arkade payments the settlement transaction is the transaction that
    /// created the VTXO on the invoice's contract, recorded as the payment's outpoint.
    /// </summary>
    private string? TryGetArkadeTransactionId(IPaymentMethodHandler? handler, PaymentEntity payment)
    {
        if (handler is not ArkadePaymentMethodHandler arkadeHandler || payment.Details is null)
            return null;

        try
        {
            var outpoint = arkadeHandler.ParsePaymentDetails(payment.Details).Outpoint;
            var separator = outpoint.IndexOf(':');
            return separator > 0 ? outpoint[..separator] : outpoint;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not parse Arkade payment details for payment {PaymentId}", payment.Id);
            return null;
        }
    }
}
