using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkAutomatedPayoutProcessor: BaseAutomatedPayoutProcessor<ArkAutomatedPayoutBlob>
{
    private readonly IClientTransport _clientTransport;
    private readonly ArkadeSpendingService _arkSpendingService;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlers;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
    private readonly ArkPayoutFulfillmentService _payoutFulfillment;

    public ArkAutomatedPayoutProcessor(
        IClientTransport clientTransport,
        ILoggerFactory logger,
        StoreRepository storeRepository,
        PayoutProcessorData payoutProcessorSettings,
        ApplicationDbContextFactory applicationDbContextFactory,
        PaymentMethodHandlerDictionary paymentHandlers,
        IPluginHookService pluginHookService,
        EventAggregator eventAggregator,
        ArkadeSpendingService arkSpendingService,
        PayoutMethodHandlerDictionary payoutMethodHandlers,
        BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
        ArkPayoutFulfillmentService payoutFulfillment,
        IWalletProvider walletProvider
    )
        : base(ArkadePlugin.ArkadePaymentMethodId, logger, storeRepository, payoutProcessorSettings, applicationDbContextFactory, paymentHandlers, pluginHookService, eventAggregator)
    {
        _clientTransport = clientTransport;
        _arkSpendingService = arkSpendingService;
        _payoutMethodHandlers = payoutMethodHandlers;
        _jsonSerializerSettings = jsonSerializerSettings;
        _payoutFulfillment = payoutFulfillment;
    }

    protected override async Task Process(object paymentMethodConfig, List<PayoutData> payouts)
    {
        var payoutHandler = (ArkPayoutHandler)_payoutMethodHandlers[ArkadePlugin.ArkadePayoutMethodId];
        
        var terms = await _clientTransport.GetServerInfoAsync();

        var storeData = await _storeRepository.FindStore(PayoutProcessorSettings.StoreId) ??
            throw new InvalidOperationException("Could not find store by StoreId");

        foreach (var payout in payouts)
        {
            if (payout.GetPayoutMethodId() != PayoutMethodId)
                continue;

            if (payout.Proof is not null)
                continue;

            var amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
            if (amount < terms.Dust)
            {
                payout.State = PayoutState.Cancelled;
                continue;
            }

            var blob = payout.GetBlob(_jsonSerializerSettings);
            var claim = await payoutHandler.ParseClaimDestination(blob.Destination, CancellationToken.None);
            var destinationBip21 = await payoutHandler.TryGenerateBip21(payout, claim);

            if (destinationBip21 is null)
            {
                payout.State = PayoutState.Cancelled;
                continue;
            }

            try
            {
                // A bitcoin destination is settled through an Ark->BTC chain swap, whose
                // id is only a swap *initiation* — not delivered funds. The fulfillment
                // service completes the payout only for a real on-ledger txid; a swap id
                // leaves it InProgress until the swap settles, so we never report
                // undelivered funds as paid. It persists proof + state immediately — a
                // crash before the base class's batch save must not re-pay on restart.
                var result = await _payoutFulfillment.FulfillPayouts(
                    [payout.Id],
                    ct => _arkSpendingService.Spend(storeData, destinationBip21, ct),
                    CancellationToken.None);

                if (!result.Executed)
                    continue; // Another payment path owns this payout right now.

                // Mirror the persisted outcome onto the tracked entity so the base
                // class's batch save after Process returns doesn't overwrite it.
                var outcome = result.Outcomes.Single();
                payoutHandler.SetProofBlob(payout, outcome.Proof);
                if (outcome.State is { } state)
                    payout.State = state;
            }
            catch (Exception e)
            {
                Logs.PayServer.LogError(e,
                    "Automated Arkade payout {PayoutId} to {Destination} failed to settle",
                    payout.Id, destinationBip21);
            }
        }
    }
}
