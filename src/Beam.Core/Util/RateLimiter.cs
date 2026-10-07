using System.Diagnostics;

namespace Beam.Core.Util;

/// <summary>
/// Paces a byte stream (shared by all transfers that use it) to a rate read on every call,
/// so changing the limit — e.g. after upgrading — applies to transfers already running.
/// </summary>
public sealed class RateLimiter
{
    private static readonly double MaxBurstSeconds = 0.25;

    private readonly Func<long> _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private double _nextFree; // seconds on _clock when the budget is next available

    /// <param name="bytesPerSecond">Current limit; 0 or less means unlimited.</param>
    public RateLimiter(Func<long> bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
    }

    public long BytesPerSecond => Math.Max(0, _bytesPerSecond());

    /// <summary>Waits until <paramref name="bytes"/> more bytes may be sent.</summary>
    public Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        var rate = BytesPerSecond;
        TimeSpan delay;
        lock (_gate)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (rate == 0)
            {
                _nextFree = now;
                return Task.CompletedTask;
            }

            // Unused time beyond a short burst is not saved up.
            if (_nextFree < now - MaxBurstSeconds) _nextFree = now - MaxBurstSeconds;
            _nextFree += (double)bytes / rate;
            delay = TimeSpan.FromSeconds(_nextFree - now);
        }

        return delay > TimeSpan.FromMilliseconds(1) ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
    }
}
