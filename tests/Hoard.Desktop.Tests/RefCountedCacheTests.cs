using System.Collections.Concurrent;
using Hoard.Desktop.Infrastructure;
using Xunit;

namespace Hoard.Desktop.Tests;

public class RefCountedCacheTests
{
    private sealed class Probe
    {
        public required string Key;
        public bool Disposed;
    }

    private sealed class Harness
    {
        public RefCountedCache<Probe> Cache = null!;
        public int Loads;
        public int Disposes;

        public static Harness Build(System.Func<string, Probe?>? loader = null)
        {
            var h = new Harness();
            h.Cache = new RefCountedCache<Probe>(
                load: key =>
                {
                    Interlocked.Increment(ref h.Loads);
                    return loader is not null ? loader(key) : new Probe { Key = key };
                },
                dispose: p =>
                {
                    Interlocked.Increment(ref h.Disposes);
                    p.Disposed = true;
                });
            return h;
        }
    }

    [Fact]
    public void Acquire_loads_once_and_exposes_value()
    {
        var h = Harness.Build();
        using var lease = h.Cache.Acquire("hello");
        Assert.NotNull(lease);
        Assert.Equal("hello", lease!.Value.Key);
        Assert.Equal(1, h.Loads);
        Assert.Equal(1, h.Cache.LiveCount);
    }

    [Fact]
    public void Two_acquires_share_one_load_and_refcount()
    {
        var h = Harness.Build();
        var a = h.Cache.Acquire("x")!;
        var b = h.Cache.Acquire("x")!;

        Assert.Equal(1, h.Loads);          // decoded once
        Assert.Same(a.Value, b.Value);     // shared instance
        Assert.Equal(1, h.Cache.LiveCount);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Frees_only_when_the_last_lease_is_released()
    {
        var h = Harness.Build();
        var a = h.Cache.Acquire("x")!;
        var b = h.Cache.Acquire("x")!;

        a.Dispose();
        Assert.Equal(0, h.Disposes);       // still held by b
        Assert.Equal(1, h.Cache.LiveCount);

        b.Dispose();
        Assert.Equal(1, h.Disposes);       // last lease → freed
        Assert.Equal(0, h.Cache.LiveCount);
        Assert.True(a.Value.Disposed);
    }

    [Fact]
    public void Lease_dispose_is_idempotent()
    {
        var h = Harness.Build();
        var a = h.Cache.Acquire("x")!;
        var b = h.Cache.Acquire("x")!;

        a.Dispose();
        a.Dispose();
        a.Dispose();                       // extra disposes must not over-release

        Assert.Equal(0, h.Disposes);
        Assert.Equal(1, h.Cache.LiveCount); // b still holds it
        b.Dispose();
        Assert.Equal(1, h.Disposes);
    }

    [Fact]
    public void Reacquire_after_full_release_loads_a_fresh_value()
    {
        var h = Harness.Build();
        var a = h.Cache.Acquire("x")!;
        var first = a.Value;
        a.Dispose();
        Assert.Equal(0, h.Cache.LiveCount);

        var b = h.Cache.Acquire("x")!;
        Assert.Equal(2, h.Loads);          // was freed, so decoded again
        Assert.NotSame(first, b.Value);
        b.Dispose();
    }

    [Fact]
    public void TryAcquire_misses_then_leases_without_loading()
    {
        var h = Harness.Build();
        Assert.Null(h.Cache.TryAcquire("x")); // nothing loaded yet

        var a = h.Cache.Acquire("x")!;
        var t = h.Cache.TryAcquire("x");
        Assert.NotNull(t);
        Assert.Equal(1, h.Loads);             // TryAcquire didn't decode
        Assert.Same(a.Value, t!.Value);
        a.Dispose();
        t.Dispose();
        Assert.Equal(0, h.Cache.LiveCount);
    }

    [Fact]
    public void Null_load_returns_null_and_caches_nothing()
    {
        var h = Harness.Build(_ => null);
        Assert.Null(h.Cache.Acquire("x"));
        Assert.Equal(0, h.Cache.LiveCount);
    }

    [Fact]
    public void Concurrent_acquire_of_the_same_key_loads_exactly_once()
    {
        // Slow load so many threads pile up on the same key.
        var h = Harness.Build(key => { Thread.Sleep(25); return new Probe { Key = key }; });
        var leases = new ConcurrentBag<ResourceLease<Probe>>();

        Parallel.For(0, 32, _ =>
        {
            var lease = h.Cache.Acquire("x");
            if (lease is not null) leases.Add(lease);
        });

        Assert.Equal(1, h.Loads);            // single-flight
        Assert.Equal(32, leases.Count);
        Assert.Equal(1, h.Cache.LiveCount);

        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, h.Cache.LiveCount);
        Assert.Equal(1, h.Disposes);
    }
}
