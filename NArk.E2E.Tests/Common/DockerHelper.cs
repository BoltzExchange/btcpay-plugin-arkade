using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;

namespace NArk.Tests.End2End.Common;

/// <summary>
/// Minimal docker-exec helper for this plugin's E2E tests. Mirrors the subset of
/// <c>submodules/NNark/NArk.Tests.End2End/Common/DockerHelper.cs</c> we actually call into
/// (<see cref="Exec"/> + <see cref="CreateLndInvoice"/>) without taking a transitive dependency
/// on the SDK-side <c>FulmineLiquidityHelper</c> / <c>System.Net.Http.Json</c> surface that
/// collides with <c>Microsoft.AspNet.WebApi.Client</c>'s <c>PostAsJsonAsync</c> in the
/// BTCPayServer transitive graph.
/// </summary>
public static class DockerHelper
{
    /// <summary>
    /// Container and CLI conventions of the BoltzExchange/regtest stack
    /// (submodules/regtest): bitcoind uses cookie auth inside its datadir and
    /// the funded server wallet is named "regtest"; the client-side LND is
    /// lnd-1 with its lnddir under /app/lnd.
    /// </summary>
    public const string BitcoinContainer = "boltz-bitcoind";

    public const string LndContainer = "boltz-lnd-1";

    public static readonly string[] BitcoinCliArgs =
        ["bitcoin-cli", "-regtest", "-datadir=/app/bitcoin", "-rpcwallet=regtest"];

    public static readonly string[] LndCliArgs =
        ["lncli", "--network=regtest", "--lnddir=/app/lnd"];

    public static async Task<string> Exec(string container, string[] args, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", container, .. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        return result.StandardOutput;
    }

    public static async Task MineBlocks(int count = 1, CancellationToken ct = default)
        => await Exec(BitcoinContainer, [.. BitcoinCliArgs, "-generate", count.ToString(CultureInfo.InvariantCulture)], ct);

    /// <summary>
    /// Total sats received by <paramref name="address"/> with at least
    /// <paramref name="minConf"/> confirmations, per the bitcoin container's own wallet.
    /// Returns 0 for addresses the wallet doesn't know.
    /// </summary>
    public static async Task<long> BitcoinGetReceivedByAddressSats(
        string address, int minConf = 0, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", BitcoinContainer, .. BitcoinCliArgs, "getreceivedbyaddress", address, minConf.ToString(CultureInfo.InvariantCulture)])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            return 0;
        var btc = decimal.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture);
        return (long)(btc * 100_000_000m);
    }

    /// <summary>
    /// Cancels an open invoice on the regtest LND node, making any later payment attempt fail
    /// terminally with <c>INCORRECT_PAYMENT_DETAILS</c> — the swap-failure injection.
    /// </summary>
    public static async Task CancelLndInvoice(string paymentHashHex, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", LndContainer, .. LndCliArgs, "cancelinvoice", paymentHashHex])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"cancelinvoice {paymentHashHex} failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
    }

    /// <summary>
    /// Looks an invoice up on the regtest LND node by payment hash: its state
    /// (e.g. <c>SETTLED</c>) and the amount actually paid in sats.
    /// </summary>
    public static async Task<(string State, long AmtPaidSats)> LndLookupInvoice(
        string paymentHashHex, CancellationToken ct = default)
    {
        var result = await Cli.Wrap("docker")
            .WithArguments(["exec", LndContainer, .. LndCliArgs, "lookupinvoice", paymentHashHex])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        var output = result.StandardOutput;
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(
                $"lookupinvoice {paymentHashHex} failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        var obj = JsonSerializer.Deserialize<JsonObject>(output)
                  ?? throw new InvalidOperationException($"lookupinvoice returned no JSON. Output: {output}");
        var state = obj["state"]?.GetValue<string>()
                    ?? (obj["settled"]?.GetValue<bool>() == true ? "SETTLED" : "OPEN");
        var amtNode = obj["amt_paid_sat"];
        var amtPaid = amtNode is null ? 0 : long.Parse(amtNode.ToString(), CultureInfo.InvariantCulture);
        return (state, amtPaid);
    }

    /// <summary>
    /// Pays a BOLT11 invoice from the regtest LND node. For a Boltz hold invoice this call
    /// BLOCKS until Boltz claims (i.e. the reverse swap funds), so give it a generous token.
    /// </summary>
    public static async Task<string> PayLndInvoice(string bolt11, CancellationToken ct = default)
    {
        return await Exec(LndContainer, [.. LndCliArgs, "payinvoice", "--force", bolt11], ct);
    }

    public static async Task<string> CreateLndInvoice(long amtSats = 10000, int expirySecs = 30,
        CancellationToken ct = default)
        => (await CreateLndInvoiceWithHash(amtSats, expirySecs, ct)).Bolt11;

    /// <summary>
    /// Creates an LND invoice and also returns its <c>r_hash</c> — the payment-hash encoding
    /// <c>lncli</c> expects; NBitcoin's uint256 rendering of a BOLT11 hash is byte-reversed
    /// relative to it, so don't reconstruct the hash from a swap record.
    /// </summary>
    public static async Task<(string Bolt11, string RHashHex)> CreateLndInvoiceWithHash(
        long amtSats = 10000, int expirySecs = 30, CancellationToken ct = default)
    {
        List<string> args = [.. LndCliArgs, "addinvoice", "--amt", amtSats.ToString()];
        if (expirySecs > 0)
        {
            args.AddRange(["--expiry", expirySecs.ToString(CultureInfo.InvariantCulture)]);
        }

        var output = await Exec(LndContainer, args.ToArray(), ct);
        var obj = JsonSerializer.Deserialize<JsonObject>(output)
                  ?? throw new InvalidOperationException($"Invoice creation on LND failed. Output: {output}");
        var invoice = obj["payment_request"]?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Invoice creation on LND failed. Output: {output}");
        var rHash = obj["r_hash"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"addinvoice returned no r_hash. Output: {output}");
        return (invoice.Trim(), rHash.Trim());
    }
}
