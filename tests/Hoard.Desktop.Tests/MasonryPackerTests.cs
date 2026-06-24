using System;
using System.Collections.Generic;
using System.Linq;
using Hoard.Desktop.Controls;
using Xunit;

namespace Hoard.Desktop.Tests;

public class MasonryPackerTests
{
    private const double Target = 200, Spacing = 10, MinAspect = 0.5, MaxAspect = 2.6;

    private static MasonryPacker Pack(double[] aspects, double width)
        => new(aspects, width, Target, Spacing, MinAspect, MaxAspect);

    private static MasonryPacker Pack(double[] aspects, double width, int expandedIndex, double expandedHeight)
        => new(aspects, width, Target, Spacing, MinAspect, MaxAspect, expandedIndex, expandedHeight);

    // Reference implementation: the old O(n) full scan.
    private static List<int> BruteVisible(MasonryPacker p, double top, double bottom)
    {
        var result = new List<int>();
        for (var i = 0; i < p.Count; i++)
        {
            var t = p[i];
            if (!(t.Y + t.Height < top || t.Y > bottom)) result.Add(i);
        }
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(99999)]
    public void GetVisible_matches_the_brute_force_scan(int seed)
    {
        var rng = new Random(seed);
        var n = rng.Next(0, 500);
        var aspects = Enumerable.Range(0, n).Select(_ => rng.NextDouble() * 3.0 + 0.05).ToArray();
        var width = rng.Next(150, 1600);
        var packer = Pack(aspects, width);
        var buffer = new List<int>();

        for (var t = 0; t < 25; t++)
        {
            var top = rng.NextDouble() * (packer.TotalHeight + 100) - 50;
            var bottom = top + rng.Next(0, 900);
            packer.GetVisible(top, bottom, buffer);

            var expected = BruteVisible(packer, top, bottom).OrderBy(x => x).ToArray();
            var actual = buffer.OrderBy(x => x).ToArray(); // per-column order differs; compare as a set
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Empty_packer_has_no_tiles_no_height_no_visible()
    {
        var packer = Pack(Array.Empty<double>(), 800);
        Assert.Equal(0, packer.Count);
        Assert.Equal(0, packer.TotalHeight);
        var buffer = new List<int>();
        packer.GetVisible(0, 10_000, buffer);
        Assert.Empty(buffer);
    }

    [Fact]
    public void Column_count_derives_from_width()
    {
        // (660 + 10) / (200 + 10) = 3.19 → 3 columns.
        var packer = Pack(Enumerable.Repeat(1.0, 20).ToArray(), 660);
        var distinctX = Enumerable.Range(0, packer.Count).Select(i => Math.Round(packer[i].X, 3)).Distinct().Count();
        Assert.Equal(3, distinctX);
    }

    [Fact]
    public void Narrow_width_collapses_to_a_single_column()
    {
        var packer = Pack(Enumerable.Repeat(1.0, 10).ToArray(), 150); // < one target column + spacing
        var distinctX = Enumerable.Range(0, packer.Count).Select(i => packer[i].X).Distinct().Count();
        Assert.Equal(1, distinctX);
    }

    [Fact]
    public void Tiles_in_a_column_do_not_overlap_and_are_ordered_top_down()
    {
        var rng = new Random(7);
        var aspects = Enumerable.Range(0, 200).Select(_ => rng.NextDouble() * 2 + 0.2).ToArray();
        var packer = Pack(aspects, 900);

        foreach (var column in Enumerable.Range(0, packer.Count)
                     .GroupBy(i => Math.Round(packer[i].X, 3)))
        {
            var ordered = column.Select(i => packer[i]).OrderBy(t => t.Y).ToArray();
            for (var k = 1; k < ordered.Length; k++)
                Assert.True(ordered[k].Y >= ordered[k - 1].Y + ordered[k - 1].Height - 1e-9,
                    "tiles in a column must not overlap vertically");
        }
    }

    [Fact]
    public void Aspect_ratios_are_clamped()
    {
        var packer = Pack(new[] { 0.01, 100.0 }, 400); // one extreme-wide, one extreme-tall
        var columnWidth = packer[0].Width;
        // Heights bounded by clamp range, not the raw aspect.
        Assert.InRange(packer[0].Height, columnWidth * MinAspect - 1e-6, columnWidth * MaxAspect + 1e-6);
        Assert.InRange(packer[1].Height, columnWidth * MinAspect - 1e-6, columnWidth * MaxAspect + 1e-6);
    }

    // ── Full-width expanded band (the inline image-detail expansion) ──

    [Fact]
    public void Expanded_item_is_a_full_width_band_with_the_grid_strictly_above_and_below_it()
    {
        var aspects = Enumerable.Repeat(1.0, 12).ToArray();
        const double width = 660, bandHeight = 500;
        const int expanded = 5;
        var packer = Pack(aspects, width, expanded, bandHeight);

        var band = packer[expanded];
        Assert.Equal(0, band.X, 3);              // full-width: starts at the left edge
        Assert.Equal(width, band.Width, 3);      // … and spans the whole content width
        Assert.Equal(bandHeight, band.Height, 3);

        // Every earlier item is entirely above the band; every later item is entirely below it (never beside).
        for (var i = 0; i < expanded; i++)
            Assert.True(packer[i].Y + packer[i].Height <= band.Y + 1e-6, $"item {i} should sit above the band");
        for (var i = expanded + 1; i < packer.Count; i++)
            Assert.True(packer[i].Y >= band.Y + band.Height - 1e-6, $"item {i} should sit below the band");
    }

    [Fact]
    public void GetVisible_includes_the_full_width_band_and_still_matches_brute_force()
    {
        var aspects = Enumerable.Repeat(1.0, 12).ToArray();
        var packer = Pack(aspects, 660, 5, 500);
        var band = packer[5];
        var buffer = new List<int>();

        packer.GetVisible(band.Y + 1, band.Y + 2, buffer); // a sliver inside the band
        Assert.Contains(5, buffer);

        for (var top = -50.0; top < packer.TotalHeight + 50; top += 71)
        {
            packer.GetVisible(top, top + 130, buffer);
            Assert.Equal(BruteVisible(packer, top, top + 130).OrderBy(x => x), buffer.OrderBy(x => x));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(808)]
    public void GetVisible_matches_brute_force_with_a_random_expanded_band(int seed)
    {
        var rng = new Random(seed);
        var n = rng.Next(1, 400);
        var aspects = Enumerable.Range(0, n).Select(_ => rng.NextDouble() * 3.0 + 0.05).ToArray();
        var width = rng.Next(150, 1600);
        var packer = Pack(aspects, width, rng.Next(0, n), rng.Next(200, 900));
        var buffer = new List<int>();

        for (var t = 0; t < 25; t++)
        {
            var top = rng.NextDouble() * (packer.TotalHeight + 100) - 50;
            var bottom = top + rng.Next(0, 900);
            packer.GetVisible(top, bottom, buffer);
            Assert.Equal(BruteVisible(packer, top, bottom).OrderBy(x => x).ToArray(), buffer.OrderBy(x => x).ToArray());
        }
    }

    [Fact]
    public void A_zero_height_or_out_of_range_expansion_packs_exactly_like_no_expansion()
    {
        var aspects = Enumerable.Range(0, 6).Select(i => 0.6 + i * 0.2).ToArray();
        var normal = Pack(aspects, 660);
        var zeroHeight = Pack(aspects, 660, 3, 0);  // height 0 → ignored
        var outOfRange = Pack(aspects, 660, 99, 500); // index past the end → ignored

        for (var i = 0; i < aspects.Length; i++)
        {
            Assert.Equal(normal[i].X, zeroHeight[i].X, 6);
            Assert.Equal(normal[i].Y, zeroHeight[i].Y, 6);
            Assert.Equal(normal[i].X, outOfRange[i].X, 6);
            Assert.Equal(normal[i].Y, outOfRange[i].Y, 6);
        }
    }

    // ── Band height (the inline detail band's height, fed to the packer as expandedHeight) ──

    [Fact]
    public void BandHeight_wide_reserves_the_rail_and_clamps()
    {
        const double rail = 340, spacing = 10;
        var imageArea = 1000 - rail - spacing; // image sits left of the rail
        Assert.Equal(Math.Clamp(imageArea * 0.8, 280, 760), MasonryPacker.BandHeight(1000, 0.8, spacing, rail), 6);
        Assert.Equal(760, MasonryPacker.BandHeight(1000, 100, spacing, rail), 6);  // very tall → max
        Assert.Equal(280, MasonryPacker.BandHeight(1000, 0.01, spacing, rail), 6); // very wide → min
    }

    [Fact]
    public void BandHeight_narrow_stacks_image_over_a_fixed_info_area()
    {
        // Below the stack breakpoint (640): a capped full-width image plus a fixed info area (260).
        Assert.Equal(Math.Min(400 * 1.2, 760) + 260, MasonryPacker.BandHeight(400, 1.2, 10, 340), 6);
        Assert.Equal(760 + 260, MasonryPacker.BandHeight(400, 5.0, 10, 340), 6); // image capped at 760
    }

    [Fact]
    public void BandHeight_treats_a_non_positive_aspect_as_square()
    {
        var square = MasonryPacker.BandHeight(1000, 1.0, 10, 340);
        Assert.Equal(square, MasonryPacker.BandHeight(1000, 0, 10, 340), 6);
        Assert.Equal(square, MasonryPacker.BandHeight(1000, -3, 10, 340), 6);
    }
}
