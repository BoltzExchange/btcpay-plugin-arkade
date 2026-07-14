using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Boltz.Arkade.Helpers;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Hosting;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayoutData = BTCPayServer.Data.PayoutData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Boltz.Arkade.Payouts.Ark;

public class ArkPayoutHandler : IPayoutHandler, IHasNetwork
{
    private readonly IClientTransport _clientTransport;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ArkNetworkConfig _arkNetworkConfig;

    public ArkPayoutHandler(
        IClientTransport clientTransport,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        ApplicationDbContextFactory dbContextFactory,
        BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
        BTCPayNetworkProvider networkProvider,
        ArkNetworkConfig arkNetworkConfig)
    {
        _clientTransport = clientTransport;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _dbContextFactory = dbContextFactory;
        _jsonSerializerSettings = jsonSerializerSettings;
        _networkProvider = networkProvider;
        _arkNetworkConfig = arkNetworkConfig;
    }
    public string Currency => "BTC";
    public PayoutMethodId PayoutMethodId => ArkadePlugin.ArkadePayoutMethodId;

    public bool IsSupported(StoreData storeData)
    {
        var config =
            storeData
                .GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId,
                    _paymentMethodHandlerDictionary,
                    true
                );

        return !string.IsNullOrWhiteSpace(config?.WalletId);
    }

    public Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        return Task.CompletedTask;
    }

    public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination,
        CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        try
        {
            var terms = await _clientTransport.GetServerInfoAsync(cancellationToken);

            if (destination.StartsWith("bitcoin:", StringComparison.InvariantCultureIgnoreCase))
            {
                return (new ArkUriClaimDestination(new BitcoinUrlBuilder(destination, terms.Network)), null!);
            }

            return (
                new ArkAddressClaimDestination(ArkAddress.Parse(destination),
                    terms.Network.ChainName == ChainName.Mainnet), null!);
        }
        catch
        {
            return (null!, "A valid address was not provided");
        }
    }

    public (bool valid, string? error) ValidateClaimDestination(IClaimDestination claimDestination,
        PullPaymentBlob? pullPaymentBlob)
    {
        return (true, null);
    }

    public IPayoutProof ParseProof(PayoutData? payout)
    {
        if (payout?.Proof is null)
            return null!;
        var payoutMethodId = payout.GetPayoutMethodId();
        if (payoutMethodId is null)
            return null!;

        var parseResult = ParseProofType(payout.Proof);
        if (parseResult is null)
            return null!;

        if (parseResult.Value.MaybeType == ArkPayoutProof.Type)
        {
            var res = parseResult.Value.Object.ToObject<ArkPayoutProof>(
                JsonSerializer.Create(_jsonSerializerSettings.GetSerializer(payoutMethodId))
            )!;
            if (!string.IsNullOrWhiteSpace(payout.DedupId) &&
                ArkAddress.TryParse(payout.DedupId, out var arkAddress) &&
                arkAddress is not null)
            {
                res.Link = ArkadeLinkHelper.GetAddressLink(_arkNetworkConfig, arkAddress.ToString());
            }
            return res;
        }

        return parseResult.Value.Object.ToObject<ManualPayoutProof>()!;
    }

    private static (JObject Object, string? MaybeType)? ParseProofType(string? proof)
    {
        if (proof is null)
        {
            return null;
        }

        var obj = JObject.Parse(proof);
        var type = TryParseProofType(obj);

        
        return (obj, type);
    }

    private static string? TryParseProofType(JObject? proof)
    {
        if (proof is null) return null;

        if (!proof.TryGetValue("proofType", StringComparison.InvariantCultureIgnoreCase, out var proofType))
            return null;
        
        return proofType.Value<string>();
    }

    public void StartBackgroundCheck(Action<Type[]> subscribe)
    {
    }

    public Task BackgroundCheck(object o)
    {
        return Task.CompletedTask;
    }

    public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        var terms = await _clientTransport.GetServerInfoAsync();
        return terms.Dust.ToDecimal(MoneyUnit.BTC);
    }

    public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
    {
        return new Dictionary<PayoutState, List<(string Action, string Text)>>();
    }

    public Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
    {
        return Task.FromResult<StatusMessageModel>(null!);
    }

    public async Task<IActionResult> InitiatePayment(string[] payoutIds)
    {
        var terms = await _clientTransport.GetServerInfoAsync();

        await using var ctx = _dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var payouts = await ctx.Payouts
            .Include(data => data.PullPaymentData)
            .Where(data => payoutIds.Contains(data.Id)
                           && PayoutMethodId.ToString() == data.PayoutMethodId
                           && data.State == PayoutState.AwaitingPayment)
            .ToListAsync();

        var storeId = payouts.First().StoreDataId;

        List<string> bip21s = [];

        foreach (var payout in payouts)
        {
            var blob = payout.GetBlob(_jsonSerializerSettings);
            if (payout.GetPayoutMethodId() != PayoutMethodId)
                continue;
            var claim = await ParseClaimDestination(blob.Destination, CancellationToken.None);
            var bip21 = await TryGenerateBip21(payout, claim);
            if (bip21 is not null)
                bip21s.Add(bip21);
        }

        // Redirect to Send wizard with destinations query param
        // Format: bip21Uri1,bip21Uri2,... (BIP21 URIs with ark= or lightning= parameters)
        return new RedirectToActionResult("Send", "Ark", new { storeId = storeId, destinations = string.Join(",", bip21s) });
    }

    public async Task<string?> TryGenerateBip21(PayoutData payout, (IClaimDestination destination, string error) claim)
    {
        var terms = await _clientTransport.GetServerInfoAsync();
        switch (claim.destination)
        {
            case ArkUriClaimDestination uriClaimDestination:
                uriClaimDestination.BitcoinUrl.Amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
                var newUri = new UriBuilder(uriClaimDestination.BitcoinUrl.Uri);
                BTCPayServerClient.AppendPayloadToQuery(newUri,
                    new KeyValuePair<string, object>("payout", payout.Id));
                return newUri.Uri.ToString();
            case ArkAddressClaimDestination addressClaimDestination:
                var builder = new PaymentUrlBuilder("bitcoin")
                {
                    Host = addressClaimDestination.Address.ToString(terms.Network.ChainName == ChainName.Mainnet)
                };
                builder.QueryParams.Add("amount", payout.Amount.Value.ToString());
                builder.QueryParams.Add("payout", payout.Id);
                return builder.ToString();
            default:
                return null;
        }
    }

    public BTCPayNetwork Network => _networkProvider.GetNetwork<BTCPayNetwork>(Currency);

    public void SetProofBlob(PayoutData data, ArkPayoutProof? blob)
    {
        data.SetProofBlob(blob, _jsonSerializerSettings.GetSerializer(data.GetPayoutMethodId()));
    }

    public JObject SerializeProof(ArkPayoutProof arkPayoutProof)
    {
        var serializer = JsonSerializer.Create(_jsonSerializerSettings.GetSerializer(PayoutMethodId));
        return JObject.FromObject(arkPayoutProof, serializer);
    }
}
