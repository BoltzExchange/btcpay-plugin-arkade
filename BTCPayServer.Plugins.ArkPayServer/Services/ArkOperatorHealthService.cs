using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NArk.Hosting;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Cached availability snapshot of the Arkade operator.</summary>
/// <param name="Available">True when the operator answered its last probe.</param>
/// <param name="Error">User-facing reason when unavailable; <c>null</c> when available.</param>
public sealed record ArkOperatorStatus(bool Available, string? Error);

/// <summary>
/// Singleton that tracks whether the Arkade operator (arkd) is reachable, so plugin pages
/// can show a persistent "operator unavailable" banner without each page paying the cost of
/// a fresh probe.
/// </summary>
public sealed class ArkOperatorHealthService(IHttpClientFactory httpClientFactory, ArkNetworkConfig arkNetworkConfig)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private volatile ArkOperatorStatus? _cached;
    private long _checkedAtTicks; // DateTimeOffset.UtcNow.UtcTicks — long read/write is atomic.

    /// <summary>
    /// Returns the operator status, re-probing only when the cached value is older than
    /// <see cref="CacheTtl"/>. Never throws — a probe failure becomes an unavailable status.
    /// </summary>
    public async Task<ArkOperatorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out var fresh))
            return fresh;

        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed the cache while we waited on the gate.
            if (TryGetFresh(out fresh))
                return fresh;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                var infoUri = new Uri(new Uri(arkNetworkConfig.ArkUri.TrimEnd('/') + "/"), "v1/info");
                using var request = new HttpRequestMessage(HttpMethod.Get, infoUri);
                using var response = await httpClientFactory.CreateClient().SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                return Store(response.IsSuccessStatusCode
                    ? new ArkOperatorStatus(true, null)
                    : new ArkOperatorStatus(false, ArkOperatorAvailability.UnavailableMessage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return _cached ?? new ArkOperatorStatus(true, null);
            }
            catch
            {
                return Store(new ArkOperatorStatus(false, ArkOperatorAvailability.UnavailableMessage));
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>
    /// Records a failure observed by a real operation. Flips the cached state to unavailable
    /// (and resets the TTL) only when <paramref name="ex"/> looks like an operator-unreachable
    /// failure — genuine application errors are ignored here.
    /// </summary>
    public void ReportFailure(Exception ex)
    {
        if (ArkOperatorAvailability.IsUnavailable(ex))
            Store(new ArkOperatorStatus(false, ArkOperatorAvailability.UnavailableMessage));
    }

    private bool TryGetFresh(out ArkOperatorStatus status)
    {
        var cached = _cached;
        if (cached is not null &&
            DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _checkedAtTicks) < CacheTtl.Ticks)
        {
            status = cached;
            return true;
        }

        status = null!;
        return false;
    }

    private ArkOperatorStatus Store(ArkOperatorStatus status)
    {
        _cached = status;
        Interlocked.Exchange(ref _checkedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return status;
    }
}
