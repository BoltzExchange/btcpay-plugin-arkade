namespace BTCPayServer.Plugins.Boltz.Arkade.Models;

/// <summary>
/// Everything the shared <c>_SettlementOptionCard</c> partial needs to render one
/// settlement method's radio card and its threshold/destination panel. The two
/// hosts (store settings and initial setup) differ only in these parameters, so
/// adding a future settlement method means touching a single view location.
/// </summary>
public sealed class SettlementOptionCardViewModel
{
    public required SettlementOptionModel Option { get; init; }

    /// <summary>Initial-setup mode toggles the disabled badge testid, the aria state,
    /// the requirement callout and the per-field focus/validation hooks.</summary>
    public required bool IsInitialSetup { get; init; }

    /// <summary>Whether this method's radio starts checked and its panel expanded.</summary>
    public required bool IsChecked { get; init; }

    /// <summary>Radio-group prefix that keeps ids unique across the two initial-setup
    /// forms (empty in store settings).</summary>
    public required string Prefix { get; init; }

    /// <summary>Muted helper-text class the host uses for descriptions.</summary>
    public required string MutedTextClass { get; init; }
}
