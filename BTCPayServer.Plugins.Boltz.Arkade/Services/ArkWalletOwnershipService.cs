using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services;

/// <summary>
/// Enforces the one-wallet-per-store invariant: a wallet may only ever be bound to a single
/// store, and Lightning connection strings must carry the wallet's capability token —
/// knowing a wallet ID alone must never authorize spends.
/// </summary>
public class ArkWalletOwnershipService(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IWalletStorage walletStorage)
{
    private const string LightningTokenMetadataKey = "lightning:token";

    /// <summary>
    /// Checks whether the given wallet ID is referenced by any store's Ark or LN payment method config.
    /// </summary>
    public async Task<bool> IsWalletUsedByAnyStore(string walletId, string? excludeStoreId = null)
    {
        var allStores = await storeRepository.GetStores();
        var lnPaymentMethod = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnWalletRef = $"wallet-id={walletId}";
        foreach (var store in allStores)
        {
            if (excludeStoreId != null && store.Id == excludeStoreId)
                continue;

            var arkConfig = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId, paymentMethodHandlerDictionary);
            if (arkConfig?.WalletId == walletId)
                return true;

            var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                lnPaymentMethod, paymentMethodHandlerDictionary);
            if (lnConfig?.ConnectionString?.Contains(lnWalletRef, StringComparison.OrdinalIgnoreCase) is true)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the Arkade Lightning connection string for the wallet, minting its capability
    /// token on first use.
    /// </summary>
    public async Task<string> CreateLightningConnectionString(string walletId, CancellationToken cancellationToken = default)
    {
        var token = await GetLightningToken(walletId, cancellationToken);
        if (token is null)
        {
            token = Encoders.Hex.EncodeData(RandomUtils.GetBytes(32));
            await walletStorage.SetMetadataValue(walletId, LightningTokenMetadataKey, token, cancellationToken);
        }
        return $"type=arkade;wallet-id={walletId};token={token}";
    }

    /// <summary>
    /// Validates a connection string's capability token against the wallet's stored token.
    /// Wallets without a token fail closed until Lightning is (re-)enabled for them.
    /// </summary>
    public async Task<bool> ValidateLightningToken(string walletId, string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        return await GetLightningToken(walletId, cancellationToken) == token;
    }

    private async Task<string?> GetLightningToken(string walletId, CancellationToken cancellationToken)
    {
        var wallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        return wallet?.Metadata?.GetValueOrDefault(LightningTokenMetadataKey);
    }
}
