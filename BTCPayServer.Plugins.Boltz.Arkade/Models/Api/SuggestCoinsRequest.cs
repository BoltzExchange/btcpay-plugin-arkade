using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models.Api;

/// <summary>
/// Request to suggest optimal coin selection for a destination.
/// </summary>
public class SuggestCoinsRequest
{
    /// <summary>
    /// Destination type detected from address/invoice.
    /// </summary>
    public DestinationType DestinationType { get; set; }

    /// <summary>
    /// Required amount in satoshis. Null means "send all".
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? AmountSats { get; set; }

    /// <summary>
    /// Outpoints to exclude from selection (already used elsewhere).
    /// </summary>
    [MaxLength(500)]
    public List<string>? ExcludeOutpoints { get; set; }
}
