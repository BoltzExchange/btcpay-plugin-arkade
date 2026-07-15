using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services;

/// <summary>
/// Resolves the Arkade-side settlement details of Lightning payments that were
/// received through a Boltz Lightning↔Arkade reverse swap.
/// </summary>
public class ArkadeSettlementDataProvider(
    PaymentMethodHandlerDictionary handlers,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    ILogger<ArkadeSettlementDataProvider> logger)
{
    /// <summary>
    /// Returns null for payments that did not settle through an Arkade reverse swap
    /// (e.g. a store running a regular Lightning node).
    /// </summary>
    public async Task<ArkadeSettlementData?> GetSettlementData(
        InvoiceEntity invoice,
        PaymentEntity payment,
        CancellationToken cancellation = default)
    {
        var swapId = TryGetSwapId(invoice, payment);
        if (string.IsNullOrEmpty(swapId))
            return null;

        try
        {
            var swap = (await swapStorage.GetSwaps(
                swapIds: [swapId],
                swapTypes: [ArkSwapType.ReverseSubmarine],
                cancellationToken: cancellation)).FirstOrDefault();
            if (swap is null)
                return null;

            var settlementData = new ArkadeSettlementData
            {
                SwapId = swap.SwapId,
                SettlementCurrency = swap.Route?.Destination.AssetId ?? "BTC",
                SettlementAddress = swap.Address
            };

            var vtxos = await vtxoStorage.GetVtxos(
                scripts: [swap.ContractScript],
                includeSpent: true,
                cancellationToken: cancellation);
            settlementData.SettlementTransactionId =
                vtxos.OrderBy(v => v.CreatedAt).FirstOrDefault()?.TransactionId;

            return settlementData;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "Could not load Arkade settlement data for payment {PaymentId} via swap id {SwapId}",
                payment.Id, swapId);
            return null;
        }
    }

    /// <summary>
    /// The lightning invoice id BTCPay recorded on the payment's prompt. When the store's
    /// Lightning node is the Arkade client this is the Boltz swap id (ArkLightningClient maps
    /// LightningInvoice.Id to the swap id); ids from any other node simply won't resolve in
    /// swap storage.
    /// </summary>
    private string? TryGetSwapId(InvoiceEntity invoice, PaymentEntity payment)
    {
        if (!handlers.TryGetValue(payment.PaymentMethodId, out var handler))
            return null;

        var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);
        if (prompt?.Details is null)
            return null;

        LigthningPaymentPromptDetails? promptDetails;
        try
        {
            promptDetails = handler.ParsePaymentPromptDetails(prompt.Details) as LigthningPaymentPromptDetails;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not parse payment prompt details for payment {PaymentId}", payment.Id);
            return null;
        }

        if (string.IsNullOrEmpty(promptDetails?.InvoiceId))
            return null;

        if (!string.IsNullOrEmpty(payment.Destination) &&
            string.Equals(prompt.Destination, payment.Destination, StringComparison.Ordinal))
        {
            return promptDetails.InvoiceId;
        }

        return promptDetails.PaymentHash is not null &&
               string.Equals(promptDetails.PaymentHash.ToString(), payment.Id, StringComparison.OrdinalIgnoreCase)
            ? promptDetails.InvoiceId
            : null;
    }
}
