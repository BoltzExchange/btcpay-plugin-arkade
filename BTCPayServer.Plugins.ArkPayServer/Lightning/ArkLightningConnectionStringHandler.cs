using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningConnectionStringHandler(IServiceProvider serviceProvider) : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "arkade")
        {
            error = "The key 'type' must be set to 'arkade' for ArkLightning connection strings";
            return null;
        }

        if (!kv.TryGetValue("wallet-id", out var walletId))
        {
            error = "The key 'wallet-id' is mandatory for ArkLightning connection strings";
            return null;
        }

        // The token proves the connection string was issued by this plugin for a store that
        // owns the wallet — a wallet id alone is derivable from public data and must not
        // authorize spends.
        kv.TryGetValue("token", out var token);
        var ownershipService = serviceProvider.GetRequiredService<ArkWalletOwnershipService>();
        if (!ownershipService.ValidateLightningToken(walletId, token).GetAwaiter().GetResult())
        {
            error = "The key 'token' is missing or invalid for ArkLightning connection strings";
            return null;
        }

        error = null;
        return ActivatorUtilities.CreateInstance<ArkLightningClient>(serviceProvider, network, walletId);
    }
}

