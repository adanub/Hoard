using Hoard.Desktop.Infrastructure;
using Xunit;

namespace Hoard.Desktop.Tests;

public class FillWrapMathTests
{
    // nominal 240, spacing 16 → the 256px "pitch" the card grids used before the fill behaviour.

    [Fact]
    public void Exact_fit_keeps_nominal_width()
    {
        // 3 columns exactly: 3*240 + 2*16 = 752.
        var (columns, width) = FillWrapMath.Fit(752, 240, 16);
        Assert.Equal(3, columns);
        Assert.Equal(240, width, precision: 6);
    }

    [Fact]
    public void Leftover_smaller_than_a_card_is_absorbed_by_stretching()
    {
        // 900 wide: a 4th card needs 752+256=1008, so 3 columns stretch to (900-32)/3.
        var (columns, width) = FillWrapMath.Fit(900, 240, 16);
        Assert.Equal(3, columns);
        Assert.Equal((900 - 32) / 3.0, width, precision: 6);
        Assert.True(width > 240);
        Assert.True(width < 240 + 256); // absorbed slack is always less than one more card's pitch
    }

    [Fact]
    public void One_more_pixel_than_the_next_pitch_adds_a_column()
    {
        var before = FillWrapMath.Fit(1007, 240, 16);
        var after = FillWrapMath.Fit(1008, 240, 16);
        Assert.Equal(3, before.Columns);
        Assert.Equal(4, after.Columns);
        Assert.Equal(240, after.ItemWidth, precision: 6);
    }

    [Fact]
    public void Narrower_than_one_card_shrinks_the_single_column()
    {
        var (columns, width) = FillWrapMath.Fit(180, 240, 16);
        Assert.Equal(1, columns);
        Assert.Equal(180, width, precision: 6);
    }

    [Fact]
    public void Infinite_width_does_not_stretch()
    {
        var (columns, width) = FillWrapMath.Fit(double.PositiveInfinity, 240, 16);
        Assert.Equal(int.MaxValue, columns);
        Assert.Equal(240, width, precision: 6);
    }

    [Fact]
    public void Zero_width_degrades_gracefully()
    {
        var (columns, width) = FillWrapMath.Fit(0, 240, 16);
        Assert.Equal(1, columns);
        Assert.Equal(0, width, precision: 6);
    }
}
