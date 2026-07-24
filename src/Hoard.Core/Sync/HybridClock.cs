namespace Hoard.Core.Sync;

/// <summary>
/// Hybrid logical clock (HLC) for archive ops: a wall-clock reading plus a logical counter, so ticks are
/// strictly monotonic on one device even within a millisecond or under clock regression, and observing a
/// foreign op keeps this device's next tick ahead of everything it has seen. Encoded as a fixed-width
/// sortable string — <c>{unixMs:D14}-{counter:D6}-{deviceId}</c> — so ordinal string comparison IS causal
/// comparison (device id is the deterministic tiebreak), and ops can be ordered without parsing.
/// </summary>
public sealed class HybridClock
{
    private readonly string _deviceId;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private long _ms;
    private int _counter;

    public HybridClock(string deviceId, Func<DateTimeOffset>? now = null)
    {
        _deviceId = deviceId;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Issue the next timestamp: beyond every prior tick and everything observed.</summary>
    public string Tick()
    {
        lock (_gate)
        {
            var wall = _now().ToUnixTimeMilliseconds();
            if (wall > _ms)
            {
                _ms = wall;
                _counter = 0;
            }
            else if (++_counter > 999_999)
            {
                // Counter exhausted within one ms (or under a stuck clock): borrow the next ms.
                _ms++;
                _counter = 0;
            }
            return Encode(_ms, _counter, _deviceId);
        }
    }

    /// <summary>
    /// Advance past an externally-seen timestamp (a foreign device's op, or the last persisted local op
    /// after a restart), so subsequent <see cref="Tick"/>s order after it.
    /// </summary>
    public void Observe(string hlc)
    {
        var (ms, counter, _) = Parse(hlc);
        lock (_gate)
        {
            if (ms > _ms || (ms == _ms && counter > _counter))
            {
                _ms = ms;
                _counter = counter;
            }
        }
    }

    /// <summary>Causal comparison — by construction, plain ordinal string order.</summary>
    public static int Compare(string a, string b) => string.CompareOrdinal(a, b);

    public static (long Ms, int Counter, string DeviceId) Parse(string hlc)
    {
        // {14 digits}-{6 digits}-{deviceId}
        if (hlc.Length < 22 || hlc[14] != '-' || hlc[21] != '-')
            throw new FormatException($"Not an HLC timestamp: '{hlc}'.");
        return (long.Parse(hlc[..14]), int.Parse(hlc[15..21]), hlc[22..]);
    }

    private static string Encode(long ms, int counter, string deviceId) => $"{ms:D14}-{counter:D6}-{deviceId}";
}
