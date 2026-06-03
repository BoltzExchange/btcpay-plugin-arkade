using System;
using System.Threading;
using System.Threading.Tasks;
using NArk.Swaps.Boltz.Client;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record BoltzStatus(bool Available, string? Error);

public sealed class BoltzHealthService(BoltzClient? boltzClient)
{
    public const string UnavailableMessage = "Boltz is currently offline. Try again later.";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private volatile BoltzStatus? _cached;
    private long _checkedAtTicks;

    public async Task<BoltzStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (boltzClient == null)
            return new BoltzStatus(false, UnavailableMessage);

        if (TryGetFresh(out var fresh))
            return fresh;

        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(out fresh))
                return fresh;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                var version = await boltzClient.GetVersionAsync().WaitAsync(timeout.Token);

                return Store(version != null
                    ? new BoltzStatus(true, null)
                    : new BoltzStatus(false, UnavailableMessage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return _cached ?? new BoltzStatus(true, null);
            }
            catch
            {
                return Store(new BoltzStatus(false, UnavailableMessage));
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private bool TryGetFresh(out BoltzStatus status)
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

    private BoltzStatus Store(BoltzStatus status)
    {
        _cached = status;
        Interlocked.Exchange(ref _checkedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return status;
    }
}
