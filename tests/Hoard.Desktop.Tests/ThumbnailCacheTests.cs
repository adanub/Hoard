using System;
using System.IO;
using System.Linq;
using Hoard.Desktop.Services;
using Xunit;

namespace Hoard.Desktop.Tests;

public class ThumbnailCacheTests
{
    private static string Sha(char fill) => new(fill, 64);

    [Fact]
    public void PruneStaleWidths_removes_only_other_width_cache_files()
    {
        var dir = Directory.CreateTempSubdirectory("hoard-thumbs-").FullName;
        try
        {
            var current = Path.Combine(dir, $"{Sha('a')}_{ThumbnailCache.Width}.png");
            var stale = Path.Combine(dir, $"{Sha('b')}_240.png");
            var staleTwin = Path.Combine(dir, $"{Sha('a')}_240.png");
            // Not the cache's naming — a foreign png, a hash with no width, an uppercase hash, a temp file.
            var foreign = Path.Combine(dir, "cover.png");
            var widthless = Path.Combine(dir, $"{Sha('c')}.png");
            var uppercase = Path.Combine(dir, $"{Sha('C').ToUpperInvariant()}_240.png");
            var temp = Path.Combine(dir, $"{Sha('d')}_240.png.deadbeef.tmp");
            foreach (var f in new[] { current, stale, staleTwin, foreign, widthless, uppercase, temp })
                File.WriteAllText(f, "x");

            ThumbnailCache.PruneStaleWidths(dir);

            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(staleTwin));
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(foreign));
            Assert.True(File.Exists(widthless));
            Assert.True(File.Exists(uppercase));
            Assert.True(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PruneStaleWidths_tolerates_missing_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hoard-thumbs-missing-" + Guid.NewGuid().ToString("N"));
        ThumbnailCache.PruneStaleWidths(dir); // must not throw
        Assert.False(Directory.Exists(dir));
    }
}
