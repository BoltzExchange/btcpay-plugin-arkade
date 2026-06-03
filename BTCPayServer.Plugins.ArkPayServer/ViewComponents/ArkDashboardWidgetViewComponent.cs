using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using NArk.Hosting;

namespace BTCPayServer.Plugins.ArkPayServer.ViewComponents;

public class ArkDashboardWidgetViewComponent(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlerDictionary,
    ArkNetworkConfig arkNetworkConfig,
    ArkOperatorHealthService arkOperatorHealth,
    BoltzHealthService boltzHealth) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(StoreDashboardViewModel dashboardModel)
    {
        if (string.IsNullOrEmpty(dashboardModel?.StoreId))
            return Content(string.Empty);

        var store = await storeRepository.FindStore(dashboardModel.StoreId);
        if (store == null)
            return Content(string.Empty);

        var config = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
            ArkadePlugin.ArkadePaymentMethodId, handlerDictionary);

        if (config == null || string.IsNullOrEmpty(config.WalletId))
            return Content(string.Empty);

        try
        {
            var lightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var isLightningEnabled = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                lightningPaymentMethodId, handlerDictionary) != null;
            var ct = HttpContext.RequestAborted;
            var issue = await GetPaymentServiceIssue(isLightningEnabled, ct);

            if (issue is null)
                return Content(string.Empty);

            return View(new ArkDashboardWidgetViewModel
            {
                StoreId = dashboardModel.StoreId,
                Issue = issue.Value
            });
        }
        catch
        {
            return View(new ArkDashboardWidgetViewModel
            {
                StoreId = dashboardModel.StoreId,
                Issue = ArkDashboardPaymentIssue.Arkade
            });
        }
    }

    private async Task<ArkDashboardPaymentIssue?> GetPaymentServiceIssue(
        bool isLightningEnabled,
        CancellationToken cancellationToken)
    {
        var arkOperatorStatus = await arkOperatorHealth.GetStatusAsync(cancellationToken);
        var lightningUnavailable = await IsLightningUnavailable(isLightningEnabled, cancellationToken);

        return (arkOperatorStatus.Available, lightningUnavailable) switch
        {
            (false, true) => ArkDashboardPaymentIssue.ArkadeAndLightning,
            (false, false) => ArkDashboardPaymentIssue.Arkade,
            (true, true) => ArkDashboardPaymentIssue.Lightning,
            _ => null
        };
    }

    private async Task<bool> IsLightningUnavailable(bool isLightningEnabled, CancellationToken cancellationToken)
    {
        if (!isLightningEnabled || string.IsNullOrWhiteSpace(arkNetworkConfig.BoltzUri))
            return false;

        try
        {
            var status = await boltzHealth.GetStatusAsync(cancellationToken);
            return !status.Available;
        }
        catch
        {
            return true;
        }
    }
}
