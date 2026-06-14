using System;
using System.Collections.Generic;
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
/// item's height derived from its aspect ratio, greedily packed into the shortest column. The packing
/// and visibility math lives in <see cref="MasonryPacker"/> (pure + unit-tested); this is just the
/// Avalonia shell. Only the items intersecting the viewport are realized, found via per-column binary
/// search rather than scanning every item each measure/scroll.
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

    private MasonryPacker? _packer;
    private double _cachedWidth = -1;
    private int _cachedCount = -1;
    private readonly List<int> _visible = new(); // reused per pass to avoid per-frame allocation

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = availableSize.Width;
        if (double.IsInfinity(width) || width <= 0) width = TargetColumnWidth;

        var packer = EnsurePacker(context, width);
        var viewport = context.RealizationRect;
        packer.GetVisible(viewport.Top, viewport.Bottom, _visible);
        foreach (var i in _visible)
        {
            var tile = packer[i];
            context.GetOrCreateElementAt(i).Measure(new Size(tile.Width, tile.Height));
        }

        return new Size(width, packer.TotalHeight);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (_packer is not { } packer) return finalSize;

        var viewport = context.RealizationRect;
        packer.GetVisible(viewport.Top, viewport.Bottom, _visible);
        foreach (var i in _visible)
        {
            var tile = packer[i];
            context.GetOrCreateElementAt(i).Arrange(new Rect(tile.X, tile.Y, tile.Width, tile.Height));
        }

        return finalSize;
    }

    private MasonryPacker EnsurePacker(VirtualizingLayoutContext context, double width)
    {
        var count = context.ItemCount;
        if (_packer is not null && _cachedWidth == width && _cachedCount == count)
            return _packer;

        var aspects = new double[count];
        for (var i = 0; i < count; i++)
            aspects[i] = AspectRatioOf(context.GetItemAt(i));

        _packer = new MasonryPacker(aspects, width, TargetColumnWidth, Spacing, MinAspect, MaxAspect);
        _cachedWidth = width;
        _cachedCount = count;
        return _packer;
    }

    private static double AspectRatioOf(object? item)
        => item is IMasonryItem m && m.AspectRatio > 0 ? m.AspectRatio : 1.0;
}
