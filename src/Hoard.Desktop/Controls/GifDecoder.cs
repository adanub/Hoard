using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace Hoard.Desktop.Controls;

/// <summary>
/// Decoded frames of an animated image plus their per-frame delays. Frames are Avalonia bitmaps
/// holding native memory and so must be disposed; lifetime is owned by <see cref="GifFrameCache"/>,
/// which disposes an animation once nothing is displaying it. Don't dispose frames directly.
/// </summary>
public sealed class GifAnimation
{
    public IReadOnlyList<Bitmap> Frames { get; }
    public IReadOnlyList<int> DelaysMs { get; }

    /// <summary>Approximate decoded size in bytes (its in-memory footprint).</summary>
    internal long Bytes { get; }

    private bool _disposed;

    public GifAnimation(IReadOnlyList<Bitmap> frames, IReadOnlyList<int> delaysMs)
    {
        Frames = frames;
        DelaysMs = delaysMs;

        long bytes = 0;
        foreach (var frame in frames)
        {
            var size = frame.PixelSize;
            bytes += (long)size.Width * size.Height * 4; // BGRA
        }
        Bytes = bytes;
    }

    /// <summary>Free the frames' native memory. Called by the cache when the last user releases it.</summary>
    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Defer to the UI thread at background priority so disposal happens after any in-flight render
        // that might still reference these bitmaps (avoids a use-after-dispose).
        var frames = Frames;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var frame in frames)
            {
                try { frame.Dispose(); } catch { /* ignore */ }
            }
        }, DispatcherPriority.Background);
    }
}

/// <summary>
/// Decodes animated GIF/WebP frames via SkiaSharp's <see cref="SKCodec"/> (already a dependency),
/// compositing each frame onto the previous one and snapshotting it as an Avalonia <see cref="Bitmap"/>
/// at display resolution. Avalonia's built-in bitmap can't animate, so this fills the gap.
/// </summary>
public static class GifDecoder
{
    // Frames are cached at display resolution, not native — a small on-disk GIF decodes to a huge
    // uncompressed RGBA buffer per frame, so capping the width keeps memory sane.
    private const int TargetFrameWidth = 400;

    /// <summary>Decode an animated image, or null if the file isn't animated / can't be read.</summary>
    public static GifAnimation? DecodeAnimated(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null || codec.FrameCount <= 1) return null;

            var frameInfos = codec.FrameInfo;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var buffer = new SKBitmap(info);

            var frames = new List<Bitmap>(codec.FrameCount);
            var delays = new List<int>(codec.FrameCount);
            for (var i = 0; i < codec.FrameCount; i++)
            {
                // Compositing each frame onto the prior one handles GIF disposal/transparency.
                codec.GetPixels(info, buffer.GetPixels(), new SKCodecOptions(i, i - 1));
                frames.Add(Snapshot(buffer, TargetFrameWidth));

                var duration = frameInfos is not null && i < frameInfos.Length ? frameInfos[i].Duration : 0;
                delays.Add(duration > 0 ? duration : 100);
            }
            return new GifAnimation(frames, delays);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Load a single still frame, for a non-animated file that still wants to display.</summary>
    public static GifAnimation? DecodeStatic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new GifAnimation(new[] { new Bitmap(stream) }, new[] { 0 });
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap Snapshot(SKBitmap composited, int maxWidth)
    {
        using var image = SKImage.FromBitmap(composited);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        // DecodeToWidth downscales the frame to display size (Avalonia handles the scaling).
        return maxWidth > 0 && composited.Width > maxWidth
            ? Bitmap.DecodeToWidth(ms, maxWidth)
            : new Bitmap(ms);
    }
}
