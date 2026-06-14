using System;
using System.Collections.Generic;
using System.Threading;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// A handle to a leased resource from a <see cref="RefCountedCache{T}"/>. The resource stays alive as
/// long as any lease to it is undisposed; disposing this lease releases it exactly once (idempotent and
/// thread-safe). This is the single unit of ownership — hold one, dispose it when done, never share the
/// underlying release.
/// </summary>
public sealed class ResourceLease<T> : IDisposable where T : class
{
    private readonly RefCountedCache<T> _cache;
    private readonly string _key;
    private int _disposed;

    public T Value { get; }

    internal ResourceLease(RefCountedCache<T> cache, string key, T value)
    {
        _cache = cache;
        _key = key;
        Value = value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // release once, even if Dispose is called twice
        _cache.Release(_key);
    }
}

/// <summary>
/// A process-wide registry that holds a decoded resource <b>only while it is leased</b>. Keyed by
/// string, it loads on first acquire (exactly once, even under concurrent acquirers of the same key),
/// hands out ref-counted <see cref="ResourceLease{T}"/> handles, and disposes the resource the moment
/// its last lease is released. So memory tracks live usage rather than accumulating.
///
/// Ownership lives entirely in the lease: there is no public "release" — callers acquire and dispose.
/// </summary>
public sealed class RefCountedCache<T> where T : class
{
    private readonly Func<string, T?> _load;
    private readonly Action<T> _dispose;

    private sealed class Entry
    {
        public required T Value;
        public int RefCount;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _live = new();
    private readonly Dictionary<string, object> _loadLocks = new();

    /// <param name="load">Decode/produce the resource for a key (called at most once per concurrent miss). May return null.</param>
    /// <param name="dispose">Free the resource when its last lease is released (invoked outside the lock).</param>
    public RefCountedCache(Func<string, T?> load, Action<T> dispose)
    {
        _load = load;
        _dispose = dispose;
    }

    /// <summary>Lease an already-loaded resource, or null if nothing currently holds it.</summary>
    public ResourceLease<T>? TryAcquire(string key)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                return new ResourceLease<T>(this, key, entry.Value);
            }
        }
        return null;
    }

    /// <summary>Lease the resource, loading it on a miss. Runs <c>load</c> on the calling thread; serialized per key.</summary>
    public ResourceLease<T>? Acquire(string key)
    {
        var lease = TryAcquire(key);
        if (lease is not null) return lease;

        // Single-flight: only one thread loads a given key at a time; the rest reuse its result.
        lock (LoadLock(key))
        {
            var raced = TryAcquire(key);
            if (raced is not null) return raced;

            var value = _load(key);
            if (value is null) return null;

            lock (_gate)
            {
                _live[key] = new Entry { Value = value, RefCount = 1 };
            }
            return new ResourceLease<T>(this, key, value);
        }
    }

    internal void Release(string key)
    {
        T? toDispose = null;
        lock (_gate)
        {
            if (!_live.TryGetValue(key, out var entry)) return;
            if (--entry.RefCount <= 0)
            {
                _live.Remove(key);
                toDispose = entry.Value;
            }
        }
        // Dispose outside the lock — freeing can be slow / marshalled elsewhere.
        if (toDispose is not null) _dispose(toDispose);
    }

    private object LoadLock(string key)
    {
        lock (_loadLocks)
        {
            if (!_loadLocks.TryGetValue(key, out var loadLock))
            {
                // Kept for the session (one small object per distinct key) so single-flight is never weakened by a race.
                loadLock = new object();
                _loadLocks[key] = loadLock;
            }
            return loadLock;
        }
    }

    /// <summary>Number of resources currently held alive (for tests/diagnostics).</summary>
    internal int LiveCount
    {
        get { lock (_gate) return _live.Count; }
    }
}
