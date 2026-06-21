using Hoard.Ingest.GalleryDl;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The sidecar parser's best-effort extraction of a pin's Pinterest <i>section</i> (drives auto-foldering on
/// import). The exact gallery-dl field shape is to be confirmed by a live spike; these lock the shapes we probe.
/// </summary>
public class PinterestSectionParseTests
{
    [Fact]
    public void Parses_a_nested_section_object_and_builds_its_url()
    {
        const string json =
            "{\"pin_id\":\"123\",\"board\":{\"id\":\"b1\",\"url\":\"/jane/interiors/\"}," +
            "\"section\":{\"id\":\"sec-9\",\"title\":\"Kitchen\",\"slug\":\"kitchen\"}}";

        var item = PinterestSidecarParser.Parse("x.jpg", json);

        Assert.Equal("sec-9", item.SectionId);
        Assert.Equal("Kitchen", item.SectionName);
        Assert.Equal("https://www.pinterest.com/jane/interiors/kitchen/", item.SectionUrl);
    }

    [Fact]
    public void Parses_a_flat_section_id()
    {
        var item = PinterestSidecarParser.Parse("x.jpg",
            "{\"pin_id\":\"1\",\"section_id\":\"sec-2\",\"section_title\":\"Bath\"}");

        Assert.Equal("sec-2", item.SectionId);
        Assert.Equal("Bath", item.SectionName);
    }

    [Fact]
    public void Treats_a_zero_or_absent_section_as_loose()
    {
        Assert.Null(PinterestSidecarParser.Parse("x.jpg", "{\"pin_id\":\"1\",\"section_id\":0}").SectionId);
        Assert.Null(PinterestSidecarParser.Parse("x.jpg", "{\"pin_id\":\"1\"}").SectionId);
    }
}
