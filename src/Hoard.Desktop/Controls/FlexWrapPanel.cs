using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A horizontal item group that fills the row: items <b>expand to share the available width equally</b>, and
/// when they no longer fit at their natural size they <b>wrap to a new row one at a time</b> (flex-wrap with
/// grow). Items on the same row are given equal width. <see cref="Spacing"/> is the gap between items, and
/// between rows. Used for responsive button groups (e.g. the project card's Open/Edit, the Edit popup's actions).
/// </summary>
public class FlexWrapPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlexWrapPanel, double>(nameof(Spacing), 8);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    static FlexWrapPanel()
    {
        AffectsMeasure<FlexWrapPanel>(SpacingProperty);
        AffectsArrange<FlexWrapPanel>(SpacingProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return default;

        foreach (var child in Children)
            child.Measure(Size.Infinity); // natural size

        var rows = BuildRows(availableSize.Width);

        double totalHeight = 0, maxRowWidth = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            totalHeight += rows[i].Height + (i > 0 ? Spacing : 0);
            maxRowWidth = Math.Max(maxRowWidth, rows[i].NaturalWidth);
        }

        var width = double.IsInfinity(availableSize.Width) ? maxRowWidth : availableSize.Width;
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        var rows = BuildRows(finalSize.Width);
        var index = 0;
        double y = 0;

        foreach (var row in rows)
        {
            var itemWidth = Math.Max(0, (finalSize.Width - Spacing * (row.Count - 1)) / row.Count);
            double x = 0;
            for (var i = 0; i < row.Count; i++)
            {
                Children[index++].Arrange(new Rect(x, y, itemWidth, row.Height));
                x += itemWidth + Spacing;
            }
            y += row.Height + Spacing;
        }

        return finalSize;
    }

    // Greedy wrap by natural (desired) width; equal width per row is applied at arrange time.
    private List<(int Count, double Height, double NaturalWidth)> BuildRows(double availableWidth)
    {
        var rows = new List<(int, double, double)>();
        var count = 0;
        double rowWidth = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            double w = child.DesiredSize.Width, h = child.DesiredSize.Height;
            var needed = count == 0 ? w : rowWidth + Spacing + w;

            if (count > 0 && !double.IsInfinity(availableWidth) && needed > availableWidth)
            {
                rows.Add((count, rowHeight, rowWidth)); // close the full row
                count = 1;
                rowWidth = w;
                rowHeight = h;
            }
            else
            {
                rowWidth = needed;
                rowHeight = Math.Max(rowHeight, h);
                count++;
            }
        }

        if (count > 0) rows.Add((count, rowHeight, rowWidth));
        return rows;
    }
}
