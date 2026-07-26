using System;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// The pure column math behind <see cref="Controls.FillWrapPanel"/> (kept Avalonia-free and unit-tested,
/// like <c>MasonryPacker</c>): how many nominal-width items fit a row, and the stretched width each one
/// gets so the row fills edge-to-edge — leftover space smaller than one more item is absorbed by the
/// row's items instead of gaping at the right edge.
/// </summary>
internal static class FillWrapMath
{
    /// <summary>
    /// Fit items of <paramref name="nominalItemWidth"/> into <paramref name="availableWidth"/> with
    /// <paramref name="columnSpacing"/> between columns. Below one nominal width the single column
    /// shrinks to fit (mobile-first narrow); an infinite width doesn't stretch — items stay nominal.
    /// </summary>
    public static (int Columns, double ItemWidth) Fit(double availableWidth, double nominalItemWidth, double columnSpacing)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return (int.MaxValue, nominalItemWidth);
        if (availableWidth <= 0)
            return (1, 0);

        var columns = Math.Max(1, (int)Math.Floor((availableWidth + columnSpacing) / (nominalItemWidth + columnSpacing)));
        var itemWidth = (availableWidth - (columns - 1) * columnSpacing) / columns;
        return (columns, itemWidth);
    }
}
