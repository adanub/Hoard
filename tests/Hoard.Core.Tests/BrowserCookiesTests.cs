using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The cookie choice decides whether a private board comes back at all, so the two resolutions that aren't a
/// plain pass-through are pinned here: the Gecko forks and Opera GX, both of which gallery-dl knows only if
/// it is handed a path.
/// </summary>
public class BrowserCookiesTests
{
    [Fact]
    public void Offers_opera_gx_separately_from_opera()
    {
        // GX is its own install in its own folder — picking "opera" searches a folder a GX user hasn't got,
        // finds no cookies, and reports every private board missing.
        Assert.Contains(BrowserCookies.OperaGx, BrowserCookies.Choices);
        Assert.Contains("opera", BrowserCookies.Choices);
    }

    [Fact]
    public void Resolves_opera_gx_to_operas_extractor_with_a_path()
    {
        var r = BrowserCookies.Resolve(BrowserCookies.OperaGx);
        if (!r.Found)
        {
            // No GX on this machine: it must say so rather than silently crawling logged out.
            Assert.Contains("Opera GX", r.Error);
            return;
        }
        // gallery-dl has no "opera gx" browser, but it takes a path for a profile — and GX is Chromium, so
        // Opera's own extractor reads it once pointed at the right folder.
        Assert.StartsWith("opera:", r.Spec);
        Assert.Contains("Opera GX", r.Spec);
    }

    [Theory]
    [InlineData("opera gx", "Opera GX")]
    [InlineData("OPERA GX", "Opera GX")]
    [InlineData("opera", "Opera")]
    [InlineData("firefox", "Firefox")]
    [InlineData("zen", "Zen")]
    [InlineData(null, "(none)")]
    public void Names_a_browser_the_way_a_sentence_needs_it(string? choice, string expected)
        => Assert.Equal(expected, BrowserCookies.DisplayName(choice));

    [Fact]
    public void Normalises_opera_gx_but_not_an_unoffered_browser()
    {
        Assert.Equal(BrowserCookies.OperaGx, BrowserCookies.NormaliseChoice("Opera GX"));
        Assert.Equal(BrowserCookies.None, BrowserCookies.NormaliseChoice("safari"));
    }

    [Fact]
    public void Never_warns_when_no_browser_was_chosen()
        => Assert.False(BrowserCookies.IsCookieDbLocked(BrowserCookies.None));

    [Fact]
    public void Finds_the_cookie_database_under_a_chromium_profile()
    {
        var root = NewTempDir();
        var db = Path.Combine(root, "Default", "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "x");

        Assert.Equal(db, BrowserCookies.FindChromiumCookieDb(root));
    }

    [Fact]
    public void Finds_operas_layout_where_the_root_is_the_profile()
    {
        // Opera keeps no profile subfolders: Local State sits at the root beside a Default/Network/Cookies.
        var root = NewTempDir();
        var db = Path.Combine(root, "Cookies");
        File.WriteAllText(db, "x");
        File.WriteAllText(Path.Combine(root, "Local State"), "{}");

        Assert.Equal(db, BrowserCookies.FindChromiumCookieDb(root));
    }

    [Fact]
    public void Ignores_a_cookies_file_buried_somewhere_that_is_not_a_profile()
    {
        // The candidates are checked directly rather than by walking the tree, so a browser cache holding a
        // file of the same name can't be mistaken for the database (and the cache is never walked at all).
        var root = NewTempDir();
        var decoy = Path.Combine(root, "Cache", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllText(decoy, "x");

        Assert.Null(BrowserCookies.FindChromiumCookieDb(root));
    }

    [Fact]
    public void Reports_nothing_when_the_browser_was_never_installed()
        => Assert.Null(BrowserCookies.FindChromiumCookieDb(Path.Combine(NewTempDir(), "absent")));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hoard-cookies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
