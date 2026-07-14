using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

internal record ArkadeListenedContract(ArkadePromptDetails Details, string InvoiceId);