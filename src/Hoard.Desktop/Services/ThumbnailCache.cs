using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Hoard.Desktop.Services;

/// <summary>
/// Per-project on-disk cache of small thumbnails, keyed by content hash. Avoids re-decoding (large)
/// originals every time a tile is shown: the first view decodes + caches a thumbnail, and later views
/// load the tiny cached file. Thumbnails are content-addressed so they never go stale.
/// There is exactly ONE decode width (<see cref="Width"/>), owned by the cache — a per-caller width
/// once existed (240 for card covers vs 256 for tiles) and quietly doubled every cover image's
/// thumbnail with a near-identical PNG. A context that renders smaller should downscale the cached
/// bitmap at render time (as the launcher collage does), not request another size.
/// </summary>
public sealed partial class ThumbnailCache
{
    /// <summary>The single canonical thumbnail width. Bump = old files are swept on next project open.</summary>
    public const int Width = 256;

    private readonly string _root;

    public ThumbnailCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        // Self-heal: drop cached files from any other width (a historical 240px cover path, or a future
        // Width bump) so they don't sit as near-duplicates forever. Off-thread — project open stays cheap.
        Task.Run(() => PruneStaleWidths(_root));
    }

    /// <summary>Get a thumbnail bitmap, generating and caching it from <paramref name="sourcePath"/> on a miss.</summary>
    public async Task<Bitmap?> GetAsync(string sha256, string sourcePath)
    {
        var cachePath = Path.Combine(_root, $"{sha256}_{Width}.png");
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
                var bitmap = Bitmap.DecodeToWidth(source, Width);
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

    /// <summary>Delete cached thumbnails whose width suffix isn't <see cref="Width"/>. Strictly matches the
    /// cache's own naming (<c>{sha256}_{width}.png</c>) so anything unexpected in the folder is left alone.</summary>
    internal static void PruneStaleWidths(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.png"))
            {
                var match = CacheFileName().Match(Path.GetFileName(file));
                if (!match.Success) continue;
                if (int.TryParse(match.Groups[1].Value, out var width) && width == Width) continue;
                try { File.Delete(file); } catch { /* in use / already gone — retried next open */ }
            }
        }
        catch
        {
            // Best-effort housekeeping; a failed sweep just leaves the stale files until next open.
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}_([0-9]+)\\.png$")]
    private static partial Regex CacheFileName();

    private static void TryWriteCache(Bitmap bitmap, string cachePath)
    {
        try
        {
            // Write to a temp file then move into place so a concurrent reader never sees a partial file.
            var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // Explicit PNG options: the bare Save(path) overload is obsolete as of Avalonia 12.1.
            bitmap.Save(temp, PngBitmapEncoderOptions.Default);
            File.Move(temp, cachePath, overwrite: true);
        }
        catch
        {
            // Best-effort: a cache miss next time is harmless.
        }
    }
}
