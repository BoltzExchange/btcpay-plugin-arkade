using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services.Settlement;
using BTCPayServer.Services.Invoices;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpendingService(
    ISpendingService arkadeSpender,
    IClientTransport clientTransport,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    ISwapStorage swapStorage,
    ISettlementService settlementService,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
{
    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Ark address, Bitcoin address, BIP21 URI,
    /// or Lightning BOLT11 invoice (optionally prefixed with <c>lightning:</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The Arkade transaction id for direct Arkade sends, or the swap id for sends settled
    /// through a Lightning submarine swap or an Arkade→BTC chain swap.
    /// </returns>
    public Task<SpendResult> Spend(StoreData store, string destination, CancellationToken cancellationToken)
        => Spend(store, destination, amountSats: null, inputOutpoints: null, cancellationToken);

    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination, with explicit amount and/or coin selection.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Ark address, Bitcoin address, BIP21 URI,
    /// or Lightning BOLT11 invoice (optionally prefixed with <c>lightning:</c>).
    /// </param>
    /// <param name="amountSats">
    /// Optional amount in satoshis. When provided, overrides any amount embedded in the destination
    /// (e.g. BIP21 <c>amount</c> query parameter). Required for bare Ark addresses unless the address is
    /// embedded inside a BIP21 URI with an amount. Must be omitted for Lightning destinations because the
    /// amount is fixed by the BOLT11 invoice.
    /// </param>
    /// <param name="inputOutpoints">
    /// Optional list of VTXO outpoints (in <c>txid:vout</c> form) to spend. When provided, the wallet's
    /// automatic coin selection is bypassed and only the specified coins are used as inputs. Must be
    /// omitted for Lightning destinations because the Lightning client selects its own coins.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The Arkade transaction id for direct Arkade sends, or the swap id for sends settled
    /// through a Lightning submarine swap or an Arkade→BTC chain swap.
    /// </returns>
    public async Task<SpendResult> Spend(
        StoreData store,
        string destination,
        long? amountSats,
        IReadOnlyList<string>? inputOutpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        destination = (destination ?? throw new ArgumentNullException(nameof(destination))).Trim();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            throw new IncompleteArkadeSetupException("arkade wallet setup was not done!");

        if (amountSats is < 0)
            throw new MalformedPaymentDestination("Amount must be non-negative.");

        var hasExplicitInputs = inputOutpoints is { Count: > 0 };

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Lightning destinations: BOLT11 invoice (optionally lightning: prefixed)
        if (destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase) is { } lnbolt11 &&
            BOLT11PaymentRequest.TryParse(lnbolt11, out var bolt11, terms.Network))
        {
            if (bolt11 is null)
            {
                throw new MalformedPaymentDestination();
            }

            if (amountSats.HasValue)
                throw new MalformedPaymentDestination(
                    "amountSats is not supported for Lightning destinations: amount is determined by the BOLT11 invoice.");

            if (hasExplicitInputs)
                throw new MalformedPaymentDestination(
                    "inputOutpoints is not supported for Lightning destinations: the Lightning client manages coin selection.");

            var lnConfig =
                store
                    .GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                        GetLightningPaymentMethod(),
                        paymentMethodHandlerDictionary
                    );

            if (lnConfig is null)
            {
                throw new IncompleteArkadeSetupException("lightning compatibility is not enabled");
            }

            var lnClient = paymentMethodHandlerDictionary.GetLightningHandler("BTC").CreateLightningClient(lnConfig);
            if (lnClient is not ArkLightningClient)
            {
                throw new IncompleteArkadeSetupException("lightning compatibility is not enabled");
            }

            var resp = await lnClient.Pay(bolt11.ToString(), cancellationToken);
            if (resp.Result != PayResult.Ok)
                throw new ArkadePaymentFailedException($"Payment failed: {resp.ErrorDetail}");

            var swap = (await swapStorage.GetSwaps(
                    walletIds: [config.WalletId!],
                    invoices: [bolt11.ToString()],
                    cancellationToken: cancellationToken))
                .MaxBy(s => s.CreatedAt);
            return new SpendResult(TxId: null, SwapId: swap?.SwapId);
        }

        var (btcAddress, settlementAmount) = TryResolveBitcoinSettlementDestination(destination, terms.Network);
        if (btcAddress is not null)
        {
            if (hasExplicitInputs)
                throw new MalformedPaymentDestination(
                    "inputOutpoints is not supported for Bitcoin settlement destinations.");

            var transferAmount = amountSats.HasValue ? Money.Satoshis(amountSats.Value) : settlementAmount;
            if (transferAmount is null || transferAmount == Money.Zero)
                throw new MalformedPaymentDestination(
                    "Bitcoin settlement requires an amount: provide amountSats, or include an amount in the BIP21 URI.");

            try
            {
                var result = await settlementService.InitiateTransfer(
                    new SettlementTransferRequest(
                        config.WalletId!,
                        transferAmount.Satoshi,
                        SettlementDestination.Bitcoin(btcAddress.ToString())),
                    cancellationToken);

                return new SpendResult(TxId: null, SwapId: result.TransferId);
            }
            catch (MalformedPaymentDestination)
            {
                throw;
            }
            catch (Exception e) when (e is not IncompleteArkadeSetupException && e is not OperationCanceledException)
            {
                throw new ArkadePaymentFailedException(e.Message);
            }
        }

        // Resolve destination + amount for Ark-targeted payments.
        var (arkAddress, parsedAmount) = TryResolveArkDestination(destination);
        if (arkAddress is null)
            throw new MalformedPaymentDestination();

        // Amount precedence: explicit amountSats > amount encoded in destination.
        Money? amount = amountSats.HasValue ? Money.Satoshis(amountSats.Value) : parsedAmount;
        if (amount is null || amount == Money.Zero)
            throw new MalformedPaymentDestination(
                "Amount is required: provide amountSats, or include an amount in the BIP21 URI.");

        var output = new ArkTxOut(ArkTxOutType.Vtxo, amount, arkAddress);

        try
        {
            uint256 txId;
            if (hasExplicitInputs)
            {
                var selectedCoins = await ResolveCoinsForOutpoints(config.WalletId!, inputOutpoints!, cancellationToken);
                txId = await arkadeSpender.Spend(config.WalletId!, selectedCoins, [output], cancellationToken);
            }
            else
            {
                txId = await arkadeSpender.Spend(config.WalletId!, [output], cancellationToken);
            }

            // Poll for VTXO updates on active contracts — constrain to the last few
            // minutes so wallets with large historical VTXO counts don't re-fetch everything.
            var activeContracts = await contractStorage.GetContracts(
                walletIds: [config.WalletId!], isActive: true, cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(
                activeContracts.Select(c => c.Script).ToHashSet(),
                after: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                cancellationToken);

            return new SpendResult(TxId: txId.ToString(), SwapId: null);
        }
        catch (MalformedPaymentDestination)
        {
            throw;
        }
        catch (Exception e) when (e is not ArkadePaymentFailedException && e is not OperationCanceledException)
        {
            throw new ArkadePaymentFailedException(e.Message);
        }
    }

    /// <summary>
    /// Try to parse <paramref name="destination"/> as a bare Ark address or a BIP21 URI carrying one,
    /// returning the resolved address and any embedded amount.
    /// </summary>
    private static (ArkAddress? Address, Money? Amount) TryResolveArkDestination(string destination)
    {
        // Bare Ark address (no URI scheme)
        if (ArkAddress.TryParse(destination, out var bareAddress) && bareAddress is not null)
        {
            return (bareAddress, null);
        }

        // BIP21 URI: bitcoin:<host>?ark=<addr>&amount=<btc>
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            // uri.Host is empty for bitcoin: URIs, so we must parse the host portion ourselves.
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            ArkAddress? address = null;
            if (ArkAddress.TryParse(host, out var hostAddress) && hostAddress is not null)
            {
                address = hostAddress;
            }
            else if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsAddress) && qsAddress is not null)
            {
                address = qsAddress;
            }

            if (address is null)
                return (null, null);

            return (address, ParseBip21Amount(qs["amount"]));
        }

        return (null, null);
    }

    private static (BitcoinAddress? Address, Money? Amount) TryResolveBitcoinSettlementDestination(
        string destination,
        Network network)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            if (!string.IsNullOrEmpty(qs["ark"]) || ArkAddress.TryParse(host, out _))
                return (null, null);

            var address = CreateBitcoinAddressOrNull(host, network);
            if (address is null)
                return (null, null);

            return (address, ParseBip21Amount(qs["amount"]));
        }

        return (CreateBitcoinAddressOrNull(destination, network), null);
    }

    private static Money? ParseBip21Amount(string? amountStr)
    {
        if (string.IsNullOrEmpty(amountStr))
            return null;

        return Money.TryParse(amountStr, out var amount) && amount > Money.Zero
            ? amount
            : throw new MalformedPaymentDestination("BIP21 amount is invalid.");
    }

    private static BitcoinAddress? CreateBitcoinAddressOrNull(string value, Network network)
    {
        try
        {
            return BitcoinAddress.Create(value, network);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the provided <c>txid:vout</c> outpoint strings to <see cref="ArkCoin"/>s from the wallet's
    /// available coin set. Throws if any outpoint cannot be found or is malformed.
    /// </summary>
    private async Task<ArkCoin[]> ResolveCoinsForOutpoints(
        string walletId,
        IReadOnlyList<string> outpoints,
        CancellationToken cancellationToken)
    {
        var available = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);
        var byOutpoint = available.ToDictionary(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}");
        var resolved = new List<ArkCoin>(outpoints.Count);
        var missing = new List<string>();

        foreach (var raw in outpoints)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            var parts = trimmed.Split(':');
            if (parts.Length != 2 || !uint256.TryParse(parts[0], out _) || !uint.TryParse(parts[1], out _))
            {
                throw new MalformedPaymentDestination(
                    $"Input outpoint '{trimmed}' is not a valid txid:vout string.");
            }

            if (byOutpoint.TryGetValue(trimmed, out var coin))
            {
                resolved.Add(coin);
            }
            else
            {
                missing.Add(trimmed);
            }
        }

        if (resolved.Count == 0)
            throw new ArkadePaymentFailedException(
                "None of the provided input outpoints match a spendable coin in this wallet.");

        if (missing.Count > 0)
            throw new ArkadePaymentFailedException(
                $"The following input outpoints are not spendable from this wallet: {string.Join(", ", missing)}.");

        return resolved.ToArray();
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

}

/// <summary>
/// Result of an Arkade send. Exactly one of <see cref="TxId"/> and <see cref="SwapId"/> is set:
/// <see cref="TxId"/> for direct Arkade sends, <see cref="SwapId"/> for sends settled through a
/// Lightning submarine swap or an Arkade→BTC chain swap.
/// </summary>
public record SpendResult(string? TxId, string? SwapId);
