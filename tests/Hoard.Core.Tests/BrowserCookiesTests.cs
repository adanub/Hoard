using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The cookie choice decides whether a private board comes back at all, so the resolutions that aren't a
/// plain pass-through are pinned here: the Gecko forks and Opera GX, both of which gallery-dl reads only
/// when it is handed a path.
/// </summary>
public class BrowserCookiesTests
{
    [Fact]
    public void Offers_opera_gx_separately_from_opera()
    {
        // GX is its own install in its own folder, so picking "opera" searches a folder a GX user hasn't
        // got, finds no cookies, and reports every private board missing.
        Assert.Contains(BrowserCookies.OperaGx, BrowserCookies.Choices);
        Assert.Contains("opera", BrowserCookies.Choices);
    }

    [Fact]
    public void Resolves_opera_gx_to_operas_extractor_with_a_path()
    {
        var r = BrowserCookies.Resolve(BrowserCookies.OperaGx);
        if (!r.Found)
        {
            // No GX on this machine: it has to say so rather than crawling logged out.
            Assert.Contains("Opera GX", r.Error);
            return;
        }
        // gallery-dl has no "opera gx" browser, but it takes a path in place of a profile, and GX is
        // Chromium, so Opera's own extractor reads it once pointed at the right folder.
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
}
