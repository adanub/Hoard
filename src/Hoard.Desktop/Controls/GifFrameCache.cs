using System.Collections.Generic;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Holds decoded animations <b>only while they are being displayed</b>. It's a reference-counted live
/// registry, not a retained cache: each control acquires a path (decoding once, shared across the tile
/// and detail view) and releases it when it stops; when the last user releases, the frames are freed
/// immediately. So memory tracks what's actually on screen — stop playing or navigate away and it drops.
/// Always pair an <see cref="Acquire"/>/<see cref="TryAcquire"/> with a <see cref="Release"/>.
/// </summary>
public static class GifFrameCache
{
    private sealed class Entry
    {
        public required GifAnimation Animation;
        public int RefCount;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Live = new();

    // One lock object per path so concurrent acquirers of the same GIF decode it only once.
    private static readonly Dictionary<string, object> PathLocks = new();

    /// <summary>Lease an animation that's already loaded, or null if nothing is currently displaying it.</summary>
    public static GifAnimation? TryAcquire(string path)
    {
        lock (Gate)
        {
            if (Live.TryGetValue(path, out var entry))
            {
                entry.RefCount++;
                return entry.Animation;
            }
        }
        return null;
    }

    /// <summary>Lease the animation, decoding it on a miss. Runs the decode on the calling thread.</summary>
    public static GifAnimation? Acquire(string path)
    {
        var live = TryAcquire(path);
        if (live is not null) return live;

        // Serialize per path: if another thread is already decoding this GIF, wait and reuse its result.
        lock (GetPathLock(path))
        {
            var raced = TryAcquire(path);
            if (raced is not null) return raced;

            var decoded = GifDecoder.DecodeAnimated(path) ?? GifDecoder.DecodeStatic(path);
            if (decoded is null) return null;

            lock (Gate)
            {
                Live[path] = new Entry { Animation = decoded, RefCount = 1 };
            }
            return decoded;
        }
    }

    /// <summary>Release a lease. When the last user releases a path, its frames are freed immediately.</summary>
    public static void Release(string path)
    {
        lock (Gate)
        {
            if (!Live.TryGetValue(path, out var entry)) return;
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                Live.Remove(path);
                entry.Animation.Dispose();
            }
        }
    }

    private static object GetPathLock(string path)
    {
        lock (PathLocks)
        {
            if (!PathLocks.TryGetValue(path, out var pathLock))
            {
                pathLock = new object();
                PathLocks[path] = pathLock;
            }
            return pathLock;
        }
    }
}
