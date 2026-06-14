using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

public class PinterestSidecarParserTests
{
    private const string Sample = """
    {
      "pin_id": "12345",
      "id": "12345",
      "created_at": "Tue, 21 Jun 2022 16:38:45",
      "title": "Sunset",
      "description": "A nice sunset over the hills",
      "link": "https://example.com/original-article",
      "board": { "id": "999", "name": "Nature", "url": "/jane/nature/" },
      "width": 800,
      "height": 600,
      "hashtags": ["#sunset", "nature"]
    }
    """;

    [Fact]
    public void Parses_core_fields()
    {
        var item = PinterestSidecarParser.Parse(@"C:\tmp\12345.jpg", Sample);

        Assert.Equal("pinterest", item.Connector);
        Assert.Equal("12345", item.SourceId);
        Assert.Equal("Sunset", item.Title);
        Assert.Equal("A nice sunset over the hills", item.Description);
        Assert.Equal("https://example.com/original-article", item.OriginalUrl);
        Assert.Equal(800, item.Width);
        Assert.Equal(600, item.Height);
    }

    [Fact]
    public void Builds_pin_url_and_absolute_board_url()
    {
        var item = PinterestSidecarParser.Parse("x.jpg", Sample);
        Assert.Equal("https://www.pinterest.com/pin/12345/", item.SourceUrl);
        Assert.Equal("Nature", item.BoardName);
        Assert.Equal("999", item.BoardId);
        Assert.Equal("https://www.pinterest.com/jane/nature/", item.BoardUrl);
    }

    [Fact]
    public void Parses_date_and_strips_hashes_from_tags()
    {
        var item = PinterestSidecarParser.Parse("x.jpg", Sample);
        Assert.NotNull(item.CreatedAt);
        Assert.Equal(2022, item.CreatedAt!.Value.Year);
        Assert.Equal(new[] { "sunset", "nature" }, item.Tags);
    }

    [Fact]
    public void Retains_raw_json()
    {
        var item = PinterestSidecarParser.Parse("x.jpg", Sample);
        Assert.Contains("\"pin_id\"", item.RawJson);
    }

    [Fact]
    public void Tolerates_sparse_metadata()
    {
        var item = PinterestSidecarParser.Parse("x.jpg", "{}");
        Assert.Null(item.SourceId);
        Assert.Null(item.BoardName);
        Assert.Empty(item.Tags);
    }
}
