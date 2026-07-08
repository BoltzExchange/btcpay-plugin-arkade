using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Settlement;

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
        SettlementInput? input,
        CancellationToken cancellationToken);

    ArkadePaymentMethodConfig ApplyInitialSetupDefault(
        StoreData store,
        ArkadePaymentMethodConfig config,
        SettlementInput? input);

    bool HandlesCommand(string command);

    Task<SettlementOptionUpdateResult> HandleCommand(
        string command,
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

    public static SettlementOptionUpdateResult Saved(ArkadePaymentMethodConfig config, string message) =>
        new(true, message, config);
}
