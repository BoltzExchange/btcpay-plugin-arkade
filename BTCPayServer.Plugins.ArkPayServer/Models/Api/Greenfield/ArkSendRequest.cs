using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

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
    public long? AmountSats { get; set; }

    /// <summary>
    /// Specific VTXO outpoints to use as inputs. If empty, coins are selected automatically.
    /// </summary>
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
