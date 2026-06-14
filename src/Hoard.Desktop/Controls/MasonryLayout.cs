using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Hoard.Desktop.Controls;

/// <summary>An item that knows its own aspect ratio (height ÷ width), for masonry layout.</summary>
public interface IMasonryItem
{
    double AspectRatio { get; }
}

/// <summary>
/// A virtualizing Pinterest-style masonry layout for <c>ItemsRepeater</c>: fixed-width columns, each
/// item's height derived from its aspect ratio, greedily packed into the shortest column. Positions
/// are computed analytically from item metadata (no image decoding), so the whole list can be laid out
/// while only the items intersecting the viewport are realized.
/// </summary>
public sealed class MasonryLayout : VirtualizingLayout
{
    public static readonly StyledProperty<double> TargetColumnWidthProperty =
        AvaloniaProperty.Register<MasonryLayout, double>(nameof(TargetColumnWidth), 200);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<MasonryLayout, double>(nameof(Spacing), 8);

    // Clamp extreme aspect ratios so a 1×1000 pin doesn't produce an absurdly tall tile.
    private const double MinAspect = 0.5;
    private const double MaxAspect = 2.6;

    public double TargetColumnWidth
    {
        get => GetValue(TargetColumnWidthProperty);
        set => SetValue(TargetColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private Rect[]? _bounds;
    private double _totalHeight;
    private double _cachedWidth = -1;
    private int _cachedCount = -1;

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = availableSize.Width;
        if (double.IsInfinity(width) || width <= 0) width = TargetColumnWidth;

        var bounds = EnsureBounds(context, width);

        var viewport = context.RealizationRect;
        for (var i = 0; i < bounds.Length; i++)
        {
            var b = bounds[i];
            if (b.Bottom < viewport.Top || b.Top > viewport.Bottom) continue; // outside the viewport → leave unrealized
            var element = context.GetOrCreateElementAt(i);
            element.Measure(b.Size);
        }

        return new Size(width, _totalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var bounds = _bounds;
        if (bounds is null) return finalSize;

        var viewport = context.RealizationRect;
        for (var i = 0; i < bounds.Length; i++)
        {
            var b = bounds[i];
            if (b.Bottom < viewport.Top || b.Top > viewport.Bottom) continue;
            var element = context.GetOrCreateElementAt(i);
            element.Arrange(b);
        }

        return finalSize;
    }

    private Rect[] EnsureBounds(VirtualizingLayoutContext context, double width)
    {
        var count = context.ItemCount;
        if (_bounds is not null && _cachedWidth == width && _cachedCount == count)
            return _bounds;

        var spacing = Spacing;
        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (TargetColumnWidth + spacing)));
        var columnWidth = (width - spacing * (columns - 1)) / columns;

        var columnBottoms = new double[columns];
        var bounds = new Rect[count];
        for (var i = 0; i < count; i++)
        {
            var aspect = Math.Clamp(AspectRatioOf(context.GetItemAt(i)), MinAspect, MaxAspect);
            var height = columnWidth * aspect;

            // Place into the currently shortest column.
            var col = 0;
            for (var c = 1; c < columns; c++)
                if (columnBottoms[c] < columnBottoms[col]) col = c;

            var x = col * (columnWidth + spacing);
            var y = columnBottoms[col];
            bounds[i] = new Rect(x, y, columnWidth, height);
            columnBottoms[col] = y + height + spacing;
        }

        _bounds = bounds;
        _totalHeight = columnBottoms.Length == 0 ? 0 : Math.Max(0, columnBottoms.Max() - spacing);
        _cachedWidth = width;
        _cachedCount = count;
        return bounds;
    }

    private static double AspectRatioOf(object? item)
        => item is IMasonryItem m && m.AspectRatio > 0 ? m.AspectRatio : 1.0;
}
