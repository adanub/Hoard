using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Hoard.Desktop.Infrastructure;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A clipping viewport that reveals oversized content by scrolling it sideways while active — the fix
/// for button labels wider than their button, which used to crop unreadably. The child is measured
/// unbounded; when it fits, it's arranged normally (its own alignment applies) and this control is
/// inert. When it overflows, the child is left-anchored, clipped, and — while <see cref="IsActive"/>
/// (driven from the owner's :pointerover/:pressed styles; the content itself is hit-transparent, so it
/// can't watch the pointer) — the text pans end-to-end and back in a slow ping-pong loop, snapping home
/// when the pointer leaves. The pan drives a <see cref="TranslateTransform"/>'s X directly via the
/// shared <see cref="Tween"/>: never a code-BUILT Animation on a transform (the deferred-throw gotcha
/// in CLAUDE.md). Timers stop on deactivation AND detach so a recycled/discarded control never ticks.
/// <para>An overflowing edge doesn't end at a hard cut on the container's boundary — where a rounded
/// corner would slice the glyphs — but ramps out through a <see cref="FadeWidth"/>-wide alpha gradient
/// (an <see cref="Visual.OpacityMask"/>), so the text is fully opaque only once it's clear of the
/// corner. Each side fades ONLY by as much as it currently hides, so a resting label keeps a crisp start
/// and grows its left fade in as it pans away — never a permanently dimmed first character.</para>
/// </summary>
public sealed class Marquee : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Marquee, bool>(nameof(IsActive));

    /// <summary>How far an overflowing edge takes to ramp from invisible to fully opaque. Templates bind
    /// this to the owner's corner radius: the fade should clear the curve, which is exactly where a hard
    /// crop looks worst.</summary>
    public static readonly StyledProperty<double> FadeWidthProperty =
        AvaloniaProperty.Register<Marquee, double>(nameof(FadeWidth), 12);

    private const double PixelsPerSecond = 55;   // slow enough to read while it pans
    private const double EndHoldMs = 650;        // pause at each end of the ping-pong

    private static readonly Color Opaque = Colors.White;
    private static readonly Color Clear = Color.FromArgb(0, 255, 255, 255);

    private readonly TranslateTransform _translate = new();
    private readonly Tween _tween = new();
    private readonly DispatcherTimer _hold;
    // One brush, mutated in place: the fade is re-evaluated every pan tick, and minting a brush per frame
    // would churn the renderer for no gain.
    private readonly LinearGradientBrush _fade = new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Clear, 0),
            new GradientStop(Opaque, 0),
            new GradientStop(Opaque, 1),
            new GradientStop(Clear, 1),
        },
    };
    private double _overflow;
    private bool _towardEnd = true;

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double FadeWidth
    {
        get => GetValue(FadeWidthProperty);
        set => SetValue(FadeWidthProperty, value);
    }

    public Marquee()
    {
        ClipToBounds = true;
        _hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EndHoldMs) };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (IsActive) PanLeg();
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var desired = child.DesiredSize;
        return new Size(Math.Min(desired.Width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { } child) return finalSize;
        child.RenderTransform = _translate;
        var overflow = Math.Max(0, child.DesiredSize.Width - finalSize.Width);
        if (overflow > 1)
        {
            child.Arrange(new Rect(0, 0, child.DesiredSize.Width, finalSize.Height));
        }
        else
        {
            overflow = 0;
            child.Arrange(new Rect(finalSize));
        }
        // A reflow (resize, content change) invalidates the pan geometry — restart from home.
        if (Math.Abs(overflow - _overflow) > 0.5)
        {
            _overflow = overflow;
            ResetPan();
            if (IsActive && _overflow > 0) StartPanning();
        }
        UpdateFade(finalSize.Width);
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FadeWidthProperty)
        {
            UpdateFade(Bounds.Width);
            return;
        }
        if (change.Property != IsActiveProperty) return;
        if (IsActive && _overflow > 0) StartPanning();
        else StopPanning();
    }

    /// <summary>
    /// Re-mask the viewport for the current pan position. Each side's fade is the LESSER of
    /// <see cref="FadeWidth"/> and how much that side actually hides, so the ramp grows in as content
    /// slides under the edge and is absent where nothing is cut off — a label at rest keeps a crisp
    /// first character, and a label that fits is never masked at all.
    /// </summary>
    private void UpdateFade(double width)
    {
        if (_overflow <= 0 || width <= 0 || FadeWidth <= 0)
        {
            OpacityMask = null;
            return;
        }

        var (start, end) = EdgeFade.Band(width, FadeWidth, _overflow, _translate.X);
        _fade.GradientStops[1].Offset = start;
        _fade.GradientStops[2].Offset = end;
        OpacityMask = _fade;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // No ticking on a detached/recycled control (the dead-timer rule the board views follow).
        _tween.Stop();
        _hold.Stop();
        _translate.X = 0;
        _towardEnd = true;
    }

    private void StartPanning()
    {
        _towardEnd = true;
        PanLeg();
    }

    private void StopPanning()
    {
        _tween.Stop();
        _hold.Stop();
        // Snap-with-ease back to the resting crop rather than leaving the label mid-scroll.
        _tween.Start(_translate.X, 0, 150, new CubicEaseOut(), Pan);
        _towardEnd = true;
    }

    /// <summary>One leg of the ping-pong: pan to the far end (or home), hold, then reverse.</summary>
    private void PanLeg()
    {
        var target = _towardEnd ? -_overflow : 0;
        var duration = Math.Max(500, Math.Abs(target - _translate.X) / PixelsPerSecond * 1000);
        _tween.Start(_translate.X, target, duration, new CubicEaseInOut(), Pan,
            onComplete: () =>
            {
                _towardEnd = !_towardEnd;
                if (IsActive) _hold.Start();
            });
    }

    /// <summary>The one place the pan offset moves: the edge fades track it, so they can never drift out
    /// of step with what's actually hidden.</summary>
    private void Pan(double x)
    {
        _translate.X = x;
        UpdateFade(Bounds.Width);
    }

    private void ResetPan()
    {
        _tween.Stop();
        _hold.Stop();
        _translate.X = 0;
        _towardEnd = true;
    }
}
