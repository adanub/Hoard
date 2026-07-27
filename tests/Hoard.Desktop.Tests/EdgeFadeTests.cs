using Hoard.Desktop.Infrastructure;
using Xunit;

namespace Hoard.Desktop.Tests;

/// <summary>
/// The alpha ramps a scrolling label crops behind. The rule being pinned: a side fades only by as much as
/// it actually hides, so nothing is dimmed that isn't also cut off.
/// </summary>
public class EdgeFadeTests
{
    [Fact]
    public void Content_that_fits_is_never_masked()
    {
        Assert.Equal((0d, 1d), EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 0, pan: 0));
    }

    [Fact]
    public void At_rest_only_the_hidden_trailing_edge_fades()
    {
        // The label starts where it should, so its first character must stay crisp — fading both ends
        // unconditionally is what makes a marquee look permanently washed out.
        var (start, end) = EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 40, pan: 0);
        Assert.Equal(0d, start);
        Assert.Equal(0.9d, end, 6);
    }

    [Fact]
    public void Panned_to_the_end_only_the_hidden_leading_edge_fades()
    {
        var (start, end) = EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 40, pan: -40);
        Assert.Equal(0.1d, start, 6);
        Assert.Equal(1d, end);
    }

    [Fact]
    public void Mid_pan_both_edges_fade_fully()
    {
        var (start, end) = EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 40, pan: -20);
        Assert.Equal(0.1d, start, 6);
        Assert.Equal(0.9d, end, 6);
    }

    [Fact]
    public void A_ramp_grows_in_proportion_to_what_it_hides()
    {
        // Two pixels hidden must not cost a ten-pixel fade: the ramp tracks the overhang until it reaches
        // full width, which is what makes the fade appear smoothly as the pan starts.
        var (start, _) = EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 40, pan: -3);
        Assert.Equal(0.03d, start, 6);

        // Same on the trailing side as the pan runs out.
        var (_, end) = EdgeFade.Band(width: 100, fadeWidth: 10, overflow: 40, pan: -36);
        Assert.Equal(0.96d, end, 6);
    }

    [Fact]
    public void Ramps_cap_at_half_the_viewport_so_the_stops_never_cross()
    {
        var (start, end) = EdgeFade.Band(width: 12, fadeWidth: 14, overflow: 60, pan: -30);
        Assert.Equal(0.5d, start, 6);
        Assert.Equal(0.5d, end, 6);
        Assert.True(start <= end);
    }
}
