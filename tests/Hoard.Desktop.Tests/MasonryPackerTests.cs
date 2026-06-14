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
}
