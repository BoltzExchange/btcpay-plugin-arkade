using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Boltz.Arkade.Models.Api.Greenfield;

/// <summary>
/// Request to send Ark funds to a destination.
/// </summary>
public class ArkSendRequest
{
    /// <summary>
    /// Destination: Ark address, Bitcoin address, BIP21 URI, or Lightning invoice/LNURL.
    /// </summary>
    public string Destination { get; set; } = "";

    /// <summary>
    /// Amount in satoshis. Null means "send all available".
    /// </summary>
    [Range(1, long.MaxValue)]
    public long? AmountSats { get; set; }

    /// <summary>
    /// Specific VTXO outpoints to use as inputs. If empty, coins are selected automatically.
    /// </summary>
    [MaxLength(500)]
    public List<string>? InputOutpoints { get; set; }
}

/// <summary>
/// Response after submitting a send request.
/// </summary>
public class ArkSendResponse
{
    /// <summary>
    /// Arkade transaction ID for direct Arkade sends; null when the send settles through a swap.
    /// </summary>
    public string? TxId { get; set; }

    /// <summary>
    /// Swap ID when the send settles through a Lightning submarine swap or a chain swap.
    /// </summary>
    public string? SwapId { get; set; }
}
