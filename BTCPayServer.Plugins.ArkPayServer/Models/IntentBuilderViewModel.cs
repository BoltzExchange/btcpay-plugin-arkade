namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// Represents an output destination for spending.
/// </summary>
public class SpendOutputViewModel
{
    /// <summary>
    /// The destination address, BIP21 URI, or BOLT11 invoice.
    /// </summary>
    public string Destination { get; set; } = "";

    /// <summary>
    /// Amount in BTC (optional - can be parsed from destination).
    /// </summary>
    public decimal? AmountBtc { get; set; }

    /// <summary>
    /// Amount in satoshis (computed from AmountBtc).
    /// </summary>
    public long? AmountSats => AmountBtc.HasValue ? (long)(AmountBtc.Value * 100_000_000m) : null;

    /// <summary>
    /// Output type: Vtxo (offchain) or Onchain.
    /// For Lightning, this is handled separately.
    /// </summary>
    public SpendOutputType OutputType { get; set; } = SpendOutputType.Vtxo;

    /// <summary>
    /// Whether this is a Lightning payment (BOLT11).
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// Validation error for this specific output.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Type of output for spending.
/// </summary>
public enum SpendOutputType
{
    /// <summary>
    /// Offchain VTXO output (default).
    /// </summary>
    Vtxo,

    /// <summary>
    /// Onchain Bitcoin output.
    /// </summary>
    Onchain
}

/// <summary>
/// Request for fee estimation.
/// </summary>
public class FeeEstimateRequest
{
    /// <summary>
    /// List of VTXO outpoints being spent (txid:vout format).
    /// </summary>
    public List<string> VtxoOutpoints { get; set; } = [];

    /// <summary>
    /// Total amount of inputs in satoshis.
    /// </summary>
    public long TotalInputSats { get; set; }

    /// <summary>
    /// Output destinations and amounts.
    /// </summary>
    public List<FeeEstimateOutput> Outputs { get; set; } = [];

    /// <summary>
    /// Coin selection mode ("auto" or "manual"). When "auto", server selects coins if none provided.
    /// </summary>
    public string? CoinSelectionMode { get; set; }

    /// <summary>
    /// Spend type preference ("Arkade" or "Batch").
    /// </summary>
    public string? SpendType { get; set; }
}

/// <summary>
/// Output specification for fee estimation.
/// </summary>
public class FeeEstimateOutput
{
    /// <summary>
    /// Destination address or invoice.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Amount in satoshis (optional).
    /// </summary>
    public long? AmountSats { get; set; }
}

/// <summary>
/// Response from fee estimation.
/// </summary>
public class FeeEstimateResponse
{
    /// <summary>
    /// Estimated fee in satoshis.
    /// </summary>
    public long EstimatedFeeSats { get; set; }

    /// <summary>
    /// Human-readable fee description.
    /// </summary>
    public string? FeeDescription { get; set; }

    /// <summary>
    /// Whether this is a Lightning swap (different fee structure).
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// Whether this is an Arkade→BTC chain swap (Arkade-mode Bitcoin destination).
    /// </summary>
    public bool IsChainSwap { get; set; }

    /// <summary>
    /// Fee percentage for Lightning/chain swaps.
    /// </summary>
    public decimal FeePercentage { get; set; }

    /// <summary>
    /// Miner fee for Lightning/chain swaps.
    /// </summary>
    public long MinerFeeSats { get; set; }

    /// <summary>
    /// Error message if estimation failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Total input sats from auto-selected coins (returned when server picks coins).
    /// </summary>
    public long TotalInputSats { get; set; }

    /// <summary>
    /// Number of coins selected by the server (auto mode).
    /// </summary>
    public int SelectedCoinCount { get; set; }

    /// <summary>
    /// Outpoints selected by the server (auto mode), so client can sync UI.
    /// </summary>
    public List<string>? SelectedOutpoints { get; set; }
}

/// <summary>
/// Request for server-side destination parsing (AJAX).
/// </summary>
public class ParseDestinationRequest
{
    public string Destination { get; set; } = "";
    public decimal? AmountBtc { get; set; }
}

/// <summary>
/// Response from server-side destination parsing.
/// </summary>
public class ParseDestinationResponse
{
    public string? RawBip21 { get; set; }
    public string? ResolvedAddress { get; set; }
    public string? Type { get; set; }
    public string? TypeBadge { get; set; }
    public string? TypeBadgeClass { get; set; }
    public long AmountSats { get; set; }
    public decimal AmountBtc { get; set; }
    public string? PayoutId { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public bool IsBip21 { get; set; }
    public bool IsLightning { get; set; }
    public bool IsLnurl { get; set; }
    public long LnurlMinSats { get; set; }
    public long LnurlMaxSats { get; set; }
}
