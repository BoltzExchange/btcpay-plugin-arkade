using BTCPayServer.Data;
using BTCPayServer.Plugins.Boltz.Arkade.Models;
using BTCPayServer.Plugins.Boltz.Arkade.PaymentHandler;

namespace BTCPayServer.Plugins.Boltz.Arkade.Services.Settlement;

public interface ISettlementOption
{
    StoreSettlementOption Type { get; }

    Task<SettlementOptionModel> CreateViewModel(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken);

    Task<SettlementOptionValidationResult?> ValidateInput(
        StoreData store,
        string? walletId,
        SettlementInput? input,
        CancellationToken cancellationToken);

    Task<SettlementOptionUpdateResult> Save(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input,
        CancellationToken cancellationToken);

    Task OnSaved(ArkadePaymentMethodConfig config, CancellationToken cancellationToken);
}

public record SettlementOptionValidationResult(string FieldName, string Message);

public record SettlementOptionUpdateResult(
    bool Success,
    string Message,
    ArkadePaymentMethodConfig? Config = null)
{
    public static SettlementOptionUpdateResult Error(string message) =>
        new(false, message);

    public static SettlementOptionUpdateResult Saved(
        ArkadePaymentMethodConfig config,
        string message) =>
        new(true, message, config);
}
