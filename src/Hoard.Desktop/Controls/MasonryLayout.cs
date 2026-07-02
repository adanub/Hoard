using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Hoard.Desktop.Infrastructure;

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
///
/// When one item is expanded into a full-width inline detail band (<see cref="ExpandedIndex"/>), the
/// surrounding tiles don't snap to their new positions — they're <b>tweened</b> from the collapsed packing
/// to the banded packing over a short eased animation (a virtualizing layout has no built-in reorder
/// animation), so the grid visibly reflows to make room. <see cref="ReflowSettled"/> fires when a tween
/// finishes, so the view can fade the band in only once the tiles have settled (and out before they close).
/// </summary>
public sealed class MasonryLayout : VirtualizingLayout
{
    public static readonly StyledProperty<double> TargetColumnWidthProperty =
        AvaloniaProperty.Register<MasonryLayout, double>(nameof(TargetColumnWidth), 200);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<MasonryLayout, double>(nameof(Spacing), 8);

    /// <summary>Index of the item to lay out as a full-width detail band (the inline image-detail expansion), or
    /// -1 for none. The grid packs above and below the band, never beside it. Changing it animates the reflow.</summary>
    public static readonly StyledProperty<int> ExpandedIndexProperty =
        AvaloniaProperty.Register<MasonryLayout, int>(nameof(ExpandedIndex), -1);

    // Clamp extreme aspect ratios so a 1×1000 pin doesn't produce an absurdly tall tile.
    private const double MinAspect = 0.5;
    private const double MaxAspect = 2.6;

    /// <summary>Width of the detail band's info rail. The band-height math reserves this much for the rail, and the
    /// band's XAML docks its rail at this width (<c>{x:Static MasonryLayout.RailWidth}</c>) when wide, so the
    /// layout and the height math can't drift apart.</summary>
    public const double RailWidth = 340;

    // Reflow tween: open decelerates into place; close is quicker (the user reverses it briskly).
    private const double ExpandReflowMs = 280;
    private const double CollapseReflowMs = 180;
    private static readonly IEasing ReflowEasing = new CubicEaseOut();

    static MasonryLayout()
    {
        // Changing the expanded item kicks off (or reverses) the reflow animation.
        ExpandedIndexProperty.Changed.AddClassHandler<MasonryLayout>((l, _) => l.OnExpandedChanged());
    }

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

    public int ExpandedIndex
    {
        get => GetValue(ExpandedIndexProperty);
        set => SetValue(ExpandedIndexProperty, value);
    }

    /// <summary>Raised when a reflow tween finishes: the argument is the settled expanded index (≥ 0 when the
    /// band has fully opened, -1 when it has fully closed). The view uses it to time the band's fade.</summary>
    public event Action<int>? ReflowSettled;

    /// <summary>The Y (in the layout's own coordinates) of the expanded band's top — for the view to scroll it to
    /// the viewport top. Null when nothing is expanded.</summary>
    public double? ExpandedBandTop =>
        _animIndex >= 0 && _bandPacker is { } b ? b[_animIndex].Y : null;

    private MasonryPacker? _basePacker;   // band-free packing (collapsed target)
    private MasonryPacker? _bandPacker;    // packing with the band at _animIndex (expanded target), or null
    private double[] _aspects = Array.Empty<double>();
    private double _cachedWidth = -1;
    private int _cachedCount = -1;
    private int _animIndex = -1;           // the item being expanded/collapsed (the band), or -1
    private int _bandCachedIndex = -1;     // which index _bandPacker was built for

    private double _factor;                // 0 = base geometry, 1 = band geometry
    private readonly Tween _reflow = new(); // tweens _factor between the base and banded packings

    private readonly List<int> _visible = new();   // reused per pass to avoid per-frame allocation
    private readonly List<int> _visibleB = new();  // second buffer for the band packer during a tween

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var width = availableSize.Width;
        if (double.IsInfinity(width) || width <= 0) width = TargetColumnWidth;

        EnsurePackers(context, width);
        FillVisible(context);
        foreach (var i in _visible)
        {
            var r = RectAt(i);
            context.GetOrCreateElementAt(i).Measure(new Size(r.Width, r.Height));
        }

        return new Size(width, CurrentTotalHeight());
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (_basePacker is null) return finalSize;

        FillVisible(context);
        foreach (var i in _visible)
        {
            var r = RectAt(i);
            context.GetOrCreateElementAt(i).Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
        }

        return finalSize;
    }

    // ── Reflow animation ────────────────────────────────────────────────────────

    private void OnExpandedChanged()
    {
        var e = ExpandedIndex;
        if (e >= 0)
        {
            // Expand (or switch directly to another item): (re)build the band packing for this index and tween to it.
            if (_animIndex != e) { _bandPacker = null; _bandCachedIndex = -1; }
            _animIndex = e;
            // Build the band packing NOW (from the cached aspects + width of the last measure) rather than waiting
            // for the next Measure, so ExpandedBandTop is non-null immediately — a direct A→B switch settles
            // synchronously (factor already 1) and the view reads ExpandedBandTop before the next layout pass.
            BuildBand(e);
            StartReflow(1, ExpandReflowMs);
        }
        else if (_animIndex >= 0)
        {
            // Collapse: tween back to the band-free packing, dropping the band packer once it's fully closed.
            StartReflow(0, CollapseReflowMs);
        }
        InvalidateMeasure();
    }

    private void StartReflow(double to, double durationMs)
        => _reflow.Start(_factor, to, durationMs, ReflowEasing,
            onStep: v => { _factor = v; InvalidateMeasure(); },
            onComplete: () => Settle(to));

    private void Settle(double to)
    {
        _factor = to;
        var settledIndex = to >= 1 ? _animIndex : -1;
        if (to <= 0)
        {
            _bandPacker = null;
            _bandCachedIndex = -1;
            _animIndex = -1;
        }
        InvalidateMeasure();
        ReflowSettled?.Invoke(settledIndex);
    }

    /// <summary>Stop the reflow tween — called when the owning view detaches so the timer doesn't keep ticking
    /// (and InvalidateMeasure-ing + firing ReflowSettled) on a layout that's no longer on screen.</summary>
    public void StopAnimation() => _reflow.Stop();

    // ── Geometry (lerped between the base and banded packings by _factor) ─────────

    private MasonryTile RectAt(int i)
    {
        if (_bandPacker is null || _factor <= 0) return _basePacker![i];
        if (_factor >= 1) return _bandPacker[i];
        var a = _basePacker![i];
        var b = _bandPacker[i];
        return new MasonryTile(
            Lerp(a.X, b.X), Lerp(a.Y, b.Y), Lerp(a.Width, b.Width), Lerp(a.Height, b.Height));
    }

    private double CurrentTotalHeight()
    {
        if (_basePacker is null) return 0;
        if (_bandPacker is null || _factor <= 0) return _basePacker.TotalHeight;
        if (_factor >= 1) return _bandPacker.TotalHeight;
        return Lerp(_basePacker.TotalHeight, _bandPacker.TotalHeight);
    }

    private double Lerp(double a, double b) => a + (b - a) * _factor;

    // Realize the viewport PLUS half a screen beyond each edge. During a tween a tile is mid-slide, so realize
    // the union of where it sits in BOTH the collapsed and banded packings (a tile only moves monotonically, so
    // the union is a safe superset of its in-between positions). The expanded band is always realized.
    private void FillVisible(VirtualizingLayoutContext context)
    {
        if (_basePacker is null) return;
        var viewport = context.RealizationRect;
        var buffer = double.IsFinite(viewport.Height) ? viewport.Height * 0.5 : 0;
        var top = viewport.Top - buffer;
        var bottom = viewport.Bottom + buffer;

        if (_bandPacker is null || _factor <= 0)
        {
            _basePacker.GetVisible(top, bottom, _visible);
        }
        else if (_factor >= 1)
        {
            _bandPacker.GetVisible(top, bottom, _visible);
        }
        else
        {
            _basePacker.GetVisible(top, bottom, _visible);
            _bandPacker.GetVisible(top, bottom, _visibleB);
            foreach (var i in _visibleB)
                if (!_visible.Contains(i)) _visible.Add(i);
        }

        if (_animIndex >= 0 && _animIndex < context.ItemCount && !_visible.Contains(_animIndex))
            _visible.Add(_animIndex);
    }

    private void EnsurePackers(VirtualizingLayoutContext context, double width)
    {
        var count = context.ItemCount;

        if (_basePacker is null || _cachedWidth != width || _cachedCount != count)
        {
            _aspects = new double[count];
            for (var i = 0; i < count; i++)
                _aspects[i] = AspectRatioOf(context.GetItemAt(i));
            _cachedWidth = width;
            _cachedCount = count;
            _basePacker = new MasonryPacker(_aspects, width, TargetColumnWidth, Spacing, MinAspect, MaxAspect);
            _bandPacker = null; _bandCachedIndex = -1; // rebuild the band against the new dimensions
        }

        // A stale expanded index (items removed under it) just collapses to nothing.
        if (_animIndex >= count) { _animIndex = -1; _bandPacker = null; _bandCachedIndex = -1; }

        if (_animIndex >= 0 && (_bandPacker is null || _bandCachedIndex != _animIndex))
            BuildBand(_animIndex);
    }

    // Build the banded packing for the given index from the cached aspects + width (no layout context needed), so
    // it can be produced eagerly on expand as well as during measure.
    private void BuildBand(int index)
    {
        if (_basePacker is null || _cachedWidth <= 0 || index < 0 || index >= _aspects.Length) return;
        var bandHeight = MasonryPacker.BandHeight(_cachedWidth, _aspects[index], Spacing, RailWidth);
        _bandPacker = new MasonryPacker(
            _aspects, _cachedWidth, TargetColumnWidth, Spacing, MinAspect, MaxAspect, index, bandHeight);
        _bandCachedIndex = index;
    }

    private static double AspectRatioOf(object? item)
        => item is IMasonryItem m && m.AspectRatio > 0 ? m.AspectRatio : 1.0;
}
