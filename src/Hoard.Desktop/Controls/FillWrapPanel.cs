using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Hoard.Desktop.Infrastructure;

namespace Hoard.Desktop.Controls;

/// <summary>
/// The card-grid panel: wraps like a WrapPanel but every row FILLS the width — the column count is how
/// many nominal-width cards fit (leftover smaller than one more card is absorbed, the same rule the
/// masonry applies to its tiles), and every visible child is stretched to the shared column width.
/// Spacing lives here (Column/RowSpacing), not on per-item margins, so the outer edges stay flush.
/// Collapsed children (a filtered-out card, the searching-hidden "+ New" tile) give up their slot
/// entirely rather than leaving a hole. Does not clip (ClipToBounds default false) — raised cards'
/// shadows/hover-scale need the same room as in any grid (see the CLAUDE.md raised-card-in-grid rule).
/// </summary>
public sealed class FillWrapPanel : Panel
{
    public static readonly StyledProperty<double> NominalItemWidthProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(NominalItemWidth), 240);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(ColumnSpacing), 16);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FillWrapPanel, double>(nameof(RowSpacing), 16);

    static FillWrapPanel()
    {
        AffectsMeasure<FillWrapPanel>(NominalItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double NominalItemWidth
    {
        get => GetValue(NominalItemWidthProperty);
        set => SetValue(NominalItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = FillWrapMath.Fit(availableSize.Width, NominalItemWidth, ColumnSpacing);

        double totalHeight = 0, rowHeight = 0, maxRowWidth = 0;
        var col = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            if (!TakesSlot(child)) continue;

            if (col == columns)
            {
                totalHeight += rowHeight + RowSpacing;
                rowHeight = 0;
                col = 0;
            }
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            col++;
            maxRowWidth = Math.Max(maxRowWidth, col * itemWidth + (col - 1) * ColumnSpacing);
        }
        totalHeight += rowHeight;

        // Claim the full finite width (the fill is the point); under an infinite constraint report the
        // natural extent so a scrolling host gets an honest size.
        var width = double.IsInfinity(availableSize.Width) ? maxRowWidth : availableSize.Width;
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = FillWrapMath.Fit(finalSize.Width, NominalItemWidth, ColumnSpacing);

        // Row-first pass so every child in a row shares the row's height (uniform card rows).
        var row = new List<Avalonia.Controls.Control>(columns);
        double y = 0;
        foreach (var child in Children)
        {
            if (!TakesSlot(child))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            row.Add(child);
            if (row.Count == columns)
                y += ArrangeRow(row, itemWidth, y) + RowSpacing;
        }
        if (row.Count > 0)
            y += ArrangeRow(row, itemWidth, y);
        return finalSize;
    }

    private double ArrangeRow(List<Avalonia.Controls.Control> row, double itemWidth, double y)
    {
        double rowHeight = 0;
        foreach (var child in row) rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        for (var i = 0; i < row.Count; i++)
            row[i].Arrange(new Rect(i * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));
        row.Clear();
        return rowHeight;
    }

    // A hidden child (or one whose content collapsed itself, e.g. a filtered card inside a still-visible
    // item ContentPresenter) measures to nothing — it must not hold an empty slot in the grid.
    private static bool TakesSlot(Avalonia.Controls.Control child) =>
        child.IsVisible && (child.DesiredSize.Width > 0 || child.DesiredSize.Height > 0);
}
