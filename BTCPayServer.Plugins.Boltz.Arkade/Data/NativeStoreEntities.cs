namespace BTCPayServer.Plugins.Boltz.Arkade.Data;

// Backing rows for the native boltz-client SwapStorage callback interface:
// the rust core hands swaps across the FFI as opaque serde JSON and C#
// persists them here. IsTerminal is a convenience column extracted at write
// time so listing active swaps never parses the JSON.

public class NativeSwapEntity
{
    public required string WalletId { get; set; }
    public required string SwapId { get; set; }
    public required string Json { get; set; }
    public bool IsTerminal { get; set; }
}

// Strictly monotonic per-wallet derived-key counter; incremented with a single
// atomic upsert-returning statement, never read-modify-write.
public class NativeKeyIndexEntity
{
    public required string WalletId { get; set; }
    public long NextIndex { get; set; }
}
