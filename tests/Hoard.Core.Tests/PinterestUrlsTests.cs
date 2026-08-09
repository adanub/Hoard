using Hoard.Core.Connectors;

namespace Hoard.Core.Tests;

/// <summary>
/// The section crawl URL has to come out byte-identical to the one gallery-dl builds for itself
/// (<c>root + board["url"] + "id:" + section["id"]</c>) — that identity is the whole reason an incremental
/// sync can crawl a section directly instead of hoping the board crawl reaches it.
/// </summary>
public class PinterestUrlsTests
{
    [Fact]
    public void SectionUrl_matches_gallery_dls_own_shape()
    {
        Assert.Equal(
            "https://www.pinterest.com/jane/reference-photos/id:12345",
            PinterestUrls.SectionUrl("https://www.pinterest.com/jane/reference-photos/", "12345"));
    }

    [Fact]
    public void SectionUrl_tolerates_a_board_url_without_its_trailing_slash()
    {
        Assert.Equal(
            "https://www.pinterest.com/jane/reference-photos/id:12345",
            PinterestUrls.SectionUrl("https://www.pinterest.com/jane/reference-photos", "12345"));
    }

    [Fact]
    public void SectionUrl_drops_a_shared_links_query_and_fragment()
    {
        // A board URL pasted from a Pinterest invite carries "?invite_code=…"; appending the section
        // after that would be a different, dead URL.
        Assert.Equal(
            "https://www.pinterest.com/jane/reference-photos/id:12345",
            PinterestUrls.SectionUrl("https://www.pinterest.com/jane/reference-photos/?invite_code=abc#pins", "12345"));
    }

    [Theory]
    [InlineData(null, "12345")]
    [InlineData("", "12345")]
    [InlineData("   ", "12345")]
    [InlineData("not a url", "12345")]
    [InlineData("ftp://www.pinterest.com/a/b/", "12345")]
    [InlineData("https://www.pinterest.com/", "12345")]        // the site root is not a board
    [InlineData("https://www.pinterest.com/a/b/", null)]
    [InlineData("https://www.pinterest.com/a/b/", "")]
    public void SectionUrl_returns_null_when_there_is_nothing_usable_to_build(string? boardUrl, string? sectionId)
        => Assert.Null(PinterestUrls.SectionUrl(boardUrl, sectionId));
}
