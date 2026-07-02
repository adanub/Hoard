using System;
using System.Linq;
using Hoard.Desktop.Controls;
using Xunit;

namespace Hoard.Desktop.Tests;

public class BreadcrumbTrimmerTests
{
    // 10px per char keeps the arithmetic legible; the ellipsis "…" is one char = 10px. Separators are 20px.
    private const double CharW = 10;
    private const double Sep = 20;
    private static double Measure(string s) => s.Length * CharW;

    private static string[] Titles => new[] { "Pinterest Backup", "Terrain Ideas", "Buildings" };
    // Full widths: 160 + 130 + 90 = 380, + 2 separators (40) = 420.

    [Fact]
    public void Everything_fits_untrimmed_when_there_is_room()
    {
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 420, Sep, Measure);

        Assert.Equal(new[] { 0, 1, 2 }, fit.Select(s => s.Index));
        Assert.Equal(Titles, fit.Select(s => s.Text));
    }

    [Fact]
    public void Base_segment_trims_from_its_start_with_a_leading_ellipsis()
    {
        // Tail ("Terrain Ideas › Buildings" + 2 separators around the base) costs 130+90+40 = 260, leaving 110
        // of 370 for the base: "…" + the last 10 chars = "…est Backup".
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 370, Sep, Measure);

        Assert.Equal(new[] { 0, 1, 2 }, fit.Select(s => s.Index));
        Assert.Equal("…est Backup", fit[0].Text);
        Assert.Equal("Terrain Ideas", fit[1].Text);
        Assert.Equal("Buildings", fit[2].Text);
    }

    [Fact]
    public void A_base_that_cannot_show_a_meaningful_stub_drops_and_marks_the_next_segment()
    {
        // 280 leaves 20 for the base after its 260 tail — under the 3-char minimum ("…xx" needs 40), so
        // "Pinterest Backup" drops; "Terrain Ideas" wears the marker: "…Terrain Ideas" (140) + sep + 90 = 250.
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 280, Sep, Measure);

        Assert.Equal(new[] { 1, 2 }, fit.Select(s => s.Index));
        Assert.Equal("…Terrain Ideas", fit[0].Text);
        Assert.Equal("Buildings", fit[1].Text);
    }

    [Fact]
    public void A_marked_middle_segment_trims_too_when_the_marker_alone_will_not_fit()
    {
        // 220 → base drops (its 260 tail already overflows); the middle's marker form "…Terrain Ideas" (140)
        // exceeds its remaining 110, so it trims: "…" + the last 10 chars = "…rain Ideas".
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 220, Sep, Measure);

        Assert.Equal(new[] { 1, 2 }, fit.Select(s => s.Index));
        Assert.Equal("…rain Ideas", fit[0].Text);
        Assert.Equal("Buildings", fit[1].Text);
    }

    [Fact]
    public void Only_the_current_page_survives_a_narrow_window()
    {
        // 100: no ancestor can share the row (even a 3-char stub + separator + Buildings overflows), so the
        // current title stands alone wearing the hidden-ancestors marker: "…Buildings" = 100, an exact fit.
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 100, Sep, Measure);

        Assert.Single(fit);
        Assert.Equal(2, fit[0].Index);
        Assert.Equal("…Buildings", fit[0].Text);
    }

    [Fact]
    public void The_current_page_trims_as_the_last_resort_down_to_one_character()
    {
        // 20px fits "…" + 1 char.
        var fit = BreadcrumbTrimmer.Fit(Titles, availableWidth: 20, Sep, Measure);

        Assert.Single(fit);
        Assert.Equal(2, fit[0].Index);
        Assert.Equal("…s", fit[0].Text);
    }

    [Fact]
    public void A_single_overlong_title_leading_trims()
    {
        var fit = BreadcrumbTrimmer.Fit(new[] { "Projects" }, availableWidth: 50, Sep, Measure);

        Assert.Single(fit);
        Assert.Equal(0, fit[0].Index);
        Assert.Equal("…ects", fit[0].Text); // "…" + 4 chars = 50
    }

    [Fact]
    public void Empty_input_yields_nothing()
    {
        Assert.Empty(BreadcrumbTrimmer.Fit(Array.Empty<string>(), 400, Sep, Measure));
    }

    [Fact]
    public void The_current_page_is_always_the_last_visible_segment()
    {
        foreach (var width in new[] { 420.0, 370, 280, 220, 100, 20, 5 })
        {
            var fit = BreadcrumbTrimmer.Fit(Titles, width, Sep, Measure);
            Assert.True(fit.Count >= 1);
            Assert.Equal(2, fit[^1].Index);
        }
    }
}
