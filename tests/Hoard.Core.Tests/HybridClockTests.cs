using Hoard.Core.Sync;
using Xunit;

namespace Hoard.Core.Tests;

public class HybridClockTests
{
    private const string DeviceA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DeviceB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Ticks_are_strictly_monotonic_within_one_millisecond()
    {
        var frozen = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var clock = new HybridClock(DeviceA, () => frozen);

        var a = clock.Tick();
        var b = clock.Tick();
        var c = clock.Tick();

        Assert.True(HybridClock.Compare(a, b) < 0);
        Assert.True(HybridClock.Compare(b, c) < 0);
        Assert.Equal(2, HybridClock.Parse(c).Counter); // counter advanced, wall time frozen
    }

    [Fact]
    public void Ticks_survive_a_clock_regression()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var clock = new HybridClock(DeviceA, () => now);
        var before = clock.Tick();

        now = DateTimeOffset.FromUnixTimeMilliseconds(1_000); // wall clock jumps backwards
        var after = clock.Tick();

        Assert.True(HybridClock.Compare(before, after) < 0);
    }

    [Fact]
    public void Counter_resets_when_wall_clock_advances()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var clock = new HybridClock(DeviceA, () => now);
        clock.Tick();
        clock.Tick();

        now = DateTimeOffset.FromUnixTimeMilliseconds(2_000);
        var next = clock.Tick();

        Assert.Equal(0, HybridClock.Parse(next).Counter);
        Assert.Equal(2_000, HybridClock.Parse(next).Ms);
    }

    [Fact]
    public void Observing_a_foreign_timestamp_keeps_subsequent_ticks_ahead_of_it()
    {
        // Device A's clock lags device B by 9 seconds; after seeing one of B's ops, A must still order after it.
        var clock = new HybridClock(DeviceA, () => DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var foreign = new HybridClock(DeviceB, () => DateTimeOffset.FromUnixTimeMilliseconds(10_000)).Tick();

        clock.Observe(foreign);
        var next = clock.Tick();

        Assert.True(HybridClock.Compare(foreign, next) < 0);
        Assert.Equal(DeviceA, HybridClock.Parse(next).DeviceId);
    }

    [Fact]
    public void Ordinal_string_order_matches_semantic_order_across_devices()
    {
        // The encoding's whole point: sorting op rows by the HLC string IS causal order, no parsing.
        var early = new HybridClock(DeviceB, () => DateTimeOffset.FromUnixTimeMilliseconds(1_000)).Tick();
        var late = new HybridClock(DeviceA, () => DateTimeOffset.FromUnixTimeMilliseconds(2_000)).Tick();

        Assert.True(string.CompareOrdinal(early, late) < 0);

        // Same instant on two devices: device id is the deterministic tiebreak, never an equal pair.
        var sameMsA = new HybridClock(DeviceA, () => DateTimeOffset.FromUnixTimeMilliseconds(3_000)).Tick();
        var sameMsB = new HybridClock(DeviceB, () => DateTimeOffset.FromUnixTimeMilliseconds(3_000)).Tick();
        Assert.NotEqual(0, HybridClock.Compare(sameMsA, sameMsB));
    }

    [Fact]
    public void Parse_round_trips_and_rejects_garbage()
    {
        var hlc = new HybridClock(DeviceA, () => DateTimeOffset.FromUnixTimeMilliseconds(123_456)).Tick();
        var (ms, counter, device) = HybridClock.Parse(hlc);

        Assert.Equal(123_456, ms);
        Assert.Equal(0, counter);
        Assert.Equal(DeviceA, device);
        Assert.Throws<FormatException>(() => HybridClock.Parse("not-a-timestamp"));
    }
}
