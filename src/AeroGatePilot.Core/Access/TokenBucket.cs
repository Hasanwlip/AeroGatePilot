using System.Diagnostics;

namespace AeroGatePilot.Core.Access;

/// <summary>
/// Thread-safe token bucket used to police per-client bandwidth.
/// A rate of 0 disables limiting.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly double _ticksPerSecond;
    private double _tokens;
    private long _lastTicks;

    public TokenBucket(long bytesPerSecond, Func<long>? clock = null, double? ticksPerSecond = null)
    {
        _clock = clock ?? Stopwatch.GetTimestamp;
        _ticksPerSecond = ticksPerSecond ?? Stopwatch.Frequency;
        BytesPerSecond = bytesPerSecond;
        // Burst of half a second keeps TCP flows smooth without letting a client exceed its rate noticeably.
        Capacity = Math.Max(bytesPerSecond / 2, 16 * 1024);
        _tokens = Capacity;
        _lastTicks = _clock();
    }

    public static TokenBucket FromKbps(int kbps, Func<long>? clock = null, double? ticksPerSecond = null) =>
        new(kbps <= 0 ? 0 : kbps * 1000L / 8, clock, ticksPerSecond);

    public long BytesPerSecond { get; }
    public long Capacity { get; }
    public bool Unlimited => BytesPerSecond <= 0;

    public bool TryConsume(int bytes)
    {
        if (Unlimited)
            return true;

        lock (_gate)
        {
            var now = _clock();
            var elapsed = (now - _lastTicks) / _ticksPerSecond;
            _lastTicks = now;
            _tokens = Math.Min(Capacity, _tokens + elapsed * BytesPerSecond);
            if (_tokens < bytes)
                return false;
            _tokens -= bytes;
            return true;
        }
    }
}
