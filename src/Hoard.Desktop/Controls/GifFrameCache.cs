using Hoard.Desktop.Infrastructure;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The GIF-specific binding of <see cref="RefCountedCache{T}"/>: decodes animations on demand and
/// frees their (native-memory) frames once nothing is displaying them. Acquire returns a disposable
/// <see cref="ResourceLease{T}"/> — the holder (an <see cref="AnimatedImageControl"/>) disposes it when
/// it stops showing the GIF, and that's the only way frames are released.
/// </summary>
public static class GifFrameCache
{
    private static readonly RefCountedCache<GifAnimation> Cache = new(
        load: path => GifDecoder.DecodeAnimated(path) ?? GifDecoder.DecodeStatic(path),
        dispose: animation => animation.Dispose());

    /// <summary>Lease an already-loaded GIF, or null if nothing is currently displaying it.</summary>
    public static ResourceLease<GifAnimation>? TryAcquire(string path) => Cache.TryAcquire(path);

    /// <summary>Lease the GIF, decoding on a miss (decode runs on the calling thread; serialized per path).</summary>
    public static ResourceLease<GifAnimation>? Acquire(string path) => Cache.Acquire(path);
}
