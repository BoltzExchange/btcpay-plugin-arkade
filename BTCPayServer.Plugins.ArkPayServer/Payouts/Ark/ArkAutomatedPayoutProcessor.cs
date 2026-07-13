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
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkAutomatedPayoutProcessor: BaseAutomatedPayoutProcessor<ArkAutomatedPayoutBlob>
{
    private readonly IClientTransport _clientTransport;
    private readonly ArkadeSpendingService _arkSpendingService;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlers;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
    private readonly PullPaymentHostedService _pullPaymentHostedService;

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
        PullPaymentHostedService pullPaymentHostedService,
        IWalletProvider walletProvider
    )
        : base(ArkadePlugin.ArkadePaymentMethodId, logger, storeRepository, payoutProcessorSettings, applicationDbContextFactory, paymentHandlers, pluginHookService, eventAggregator)
    {
        _clientTransport = clientTransport;
        _arkSpendingService = arkSpendingService;
        _payoutMethodHandlers = payoutMethodHandlers;
        _jsonSerializerSettings = jsonSerializerSettings;
        _pullPaymentHostedService = pullPaymentHostedService;
    }

    protected override async Task Process(object paymentMethodConfig, List<PayoutData> payouts)
    {
        var payoutHandler = (ArkPayoutHandler)_payoutMethodHandlers[ArkadePlugin.ArkadePayoutMethodId];
        
        var terms = await _clientTransport.GetServerInfoAsync();

        var storeData = await _storeRepository.FindStore(PayoutProcessorSettings.StoreId) ??
            throw new InvalidOperationException("Could not find store by StoreId");

        foreach (var payout in payouts)
        {
            if (payoutHandler.PayoutLocker.LockOrNullAsync(payout.Id, 0) is { } locker && await locker is {} disposable)
            {
                using (disposable)
                {
                    
                    var amount = new Money(payout.Amount.Value, MoneyUnit.BTC);

                    if (amount < terms.Dust)
                    {
                        payout.State = PayoutState.Cancelled;
                        continue;
                    }

                    if (payout.GetPayoutMethodId() != PayoutMethodId)
                        continue;

                    if (payout.Proof is not null)
                        continue;

                    var blob = payout.GetBlob(_jsonSerializerSettings);
                    var claim = await payoutHandler.ParseClaimDestination(blob.Destination, CancellationToken.None);
                    var destinationBip21 = await payoutHandler.TryGenerateBip21(payout, claim);

                    if (destinationBip21 is not null)
                    {
                        try
                        {
                            var result = await _arkSpendingService.Spend(storeData, destinationBip21, CancellationToken.None);

                            // A bitcoin destination is settled through an Ark->BTC chain swap, whose
                            // id is only a swap *initiation* — not delivered funds. Complete the payout
                            // only for a real on-ledger txid; a swap id leaves it InProgress until the
                            // swap settles, so we never report undelivered funds as paid.
                            var proof = ArkPayoutProof.FromSpendResult(result);
                            payoutHandler.SetProofBlob(payout, proof);
                            if (proof.ResolvedPayoutState is { } state)
                            {
                                payout.State = state;
                                // The funds just left the wallet. Persist proof + state now instead of
                                // relying on the base class's batch save after Process returns — a crash
                                // in between would leave the payout AwaitingPayment with no proof and
                                // re-pay it on restart.
                                await _pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                                {
                                    PayoutId = payout.Id,
                                    State = state,
                                    Proof = payout.GetProofBlobJson()
                                });
                            }
                        }
                        catch (Exception e)
                        {
                            Logs.PayServer.LogError(e,
                                "Automated Arkade payout {PayoutId} to {Destination} failed to settle",
                                payout.Id, destinationBip21);
                        }

                    }
                    else
                        payout.State = PayoutState.Cancelled;
                }
            }
            
        }
        

    }
}
