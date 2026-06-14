using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Hoard.Desktop.Services;

/// <summary>
/// Per-project on-disk cache of small thumbnails, keyed by content hash + width. Avoids re-decoding
/// (large) originals every time a tile is shown: the first view decodes + caches a thumbnail, and
/// later views load the tiny cached file. Thumbnails are content-addressed so they never go stale.
/// </summary>
public sealed class ThumbnailCache
{
    private readonly string _root;

    public ThumbnailCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Get a thumbnail bitmap, generating and caching it from <paramref name="sourcePath"/> on a miss.</summary>
    public async Task<Bitmap?> GetAsync(string sha256, string sourcePath, int width)
    {
        var cachePath = Path.Combine(_root, $"{sha256}_{width}.png");
        try
        {
            return await Task.Run<Bitmap?>(() =>
            {
                if (File.Exists(cachePath))
                {
                    using var cached = File.OpenRead(cachePath);
                    return new Bitmap(cached);
                }

                using var source = File.OpenRead(sourcePath);
                var bitmap = Bitmap.DecodeToWidth(source, width);
                TryWriteCache(bitmap, cachePath);
                return bitmap;
            });
        }
        catch
        {
            return null; // missing/corrupt source — caller shows a placeholder
        }
    }

    /// <summary>Delete any cached thumbnails for a content hash (across widths). Best-effort.</summary>
    public void Evict(string sha256)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, $"{sha256}_*.png"))
                File.Delete(file);
        }
        catch
        {
            // An orphaned thumbnail is harmless; it just wastes a little disk until the next clear.
        }
    }

    private static void TryWriteCache(Bitmap bitmap, string cachePath)
    {
        try
        {
            // Write to a temp file then move into place so a concurrent reader never sees a partial file.
            var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bitmap.Save(temp);
            File.Move(temp, cachePath, overwrite: true);
        }
        catch
        {
            // Best-effort: a cache miss next time is harmless.
        }
    }
}
