using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ArkadePaymentMethodHandler _handler;

    public ArkadePaymentLinkExtension(
        IServiceProvider serviceProvider,
        ArkadePaymentMethodHandler handler)
    {
        _serviceProvider = serviceProvider;
        _handler = handler;
    }
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // Get other payment methods if available
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        var amount = prompt.Calculate().Due;

        // Build BIP21 URI using the helper
        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);

        // Add the store's BTC on-chain address when that payment method is enabled.
        // Also delegate to its IPaymentLinkExtension so params other plugins
        // attached to the upstream BIP21 (PayJoin's `pj=`, Branta's `branta_*`,
        // etc.) carry through to the unified Arkade QR. Without this the Arkade
        // tab clobbers those params (issue: Branta + PayJoin lose their hooks).
        if (!string.IsNullOrEmpty(onchain?.Destination))
        {
            builder.WithOnchainAddress(onchain.Destination);

            var onchainLink = _serviceProvider.GetServices<IPaymentLinkExtension>()
                .FirstOrDefault(p => p.PaymentMethodId == onchain.PaymentMethodId);
            if (onchainLink is not null)
            {
                var upstream = onchainLink.GetPaymentLink(onchain, urlHelper);
                var qIdx = upstream?.IndexOf('?') ?? -1;
                if (qIdx >= 0)
                    builder.WithExtraQuery(upstream![(qIdx + 1)..]);
            }
        }

        // Add lightning invoice if available and within Boltz limits (prefer LN over LNURL).
        // The limits decision was made asynchronously when the prompt was configured.
        if (ShouldIncludeLightning(prompt))
        {
            if (ln is not null)
            {
                builder.WithLightning(ln.Destination);
            }
            else if (lnurl is not null && _serviceProvider.GetServices<IPaymentLinkExtension>()
                         .FirstOrDefault(p => p.PaymentMethodId == lnurl.PaymentMethodId) is {} lnurlLink)
            {
                if (lnurlLink.GetPaymentLink(lnurl, urlHelper) is { } link)
                {
                    builder.WithLightning(link.Replace("lightning:", String.Empty));
                }
            }
        }

        return builder.Build();
    }

    private bool ShouldIncludeLightning(PaymentPrompt prompt)
    {
        if (prompt.Details is null)
            return true;

        return _handler.ParsePaymentPromptDetails(prompt.Details)?.IncludeLightningInPaymentLink ?? true;
    }
}
