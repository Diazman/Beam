namespace Beam.Core.Util;

/// <summary>
/// Smoothed transfer speed from periodic (time, bytes) samples, robust to bursty progress.
/// Not thread-safe: sample from one thread (the UI timer).
/// </summary>
public sealed class SpeedMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private double _smoothed;

    public SpeedMeter(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(4);
    }

    /// <summary>Bytes per second, or 0 until enough data exists.</summary>
    public double BytesPerSecond => _smoothed;

    public void Reset()
    {
        _samples.Clear();
        _smoothed = 0;
    }

    public double Sample(long totalBytes, long? nowTicks = null)
    {
        var now = nowTicks ?? Environment.TickCount64;
        if (_samples.Count > 0 && totalBytes < _samples.Last().Bytes) Reset(); // progress went backwards (retry)
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Ticks > _window.TotalMilliseconds) _samples.Dequeue();

        var first = _samples.Peek();
        var elapsedMs = now - first.Ticks;
        if (elapsedMs < 400) return _smoothed;
        var instantaneous = (totalBytes - first.Bytes) * 1000.0 / elapsedMs;
        _smoothed = _smoothed <= 0 ? instantaneous : _smoothed * 0.7 + instantaneous * 0.3;
        return _smoothed;
    }

    /// <summary>Estimated time to transfer <paramref name="remainingBytes"/>, or null when unknown.</summary>
    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        if (_smoothed < 1) return null;
        var seconds = remainingBytes / _smoothed;
        return seconds > TimeSpan.FromDays(30).TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
    }
}
