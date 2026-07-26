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
/// </summary>
public sealed class Marquee : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Marquee, bool>(nameof(IsActive));

    private const double PixelsPerSecond = 55;   // slow enough to read while it pans
    private const double EndHoldMs = 650;        // pause at each end of the ping-pong

    private readonly TranslateTransform _translate = new();
    private readonly Tween _tween = new();
    private readonly DispatcherTimer _hold;
    private double _overflow;
    private bool _towardEnd = true;

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
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
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsActiveProperty) return;
        if (IsActive && _overflow > 0) StartPanning();
        else StopPanning();
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
        _tween.Start(_translate.X, 0, 150, new CubicEaseOut(), v => _translate.X = v);
        _towardEnd = true;
    }

    /// <summary>One leg of the ping-pong: pan to the far end (or home), hold, then reverse.</summary>
    private void PanLeg()
    {
        var target = _towardEnd ? -_overflow : 0;
        var duration = Math.Max(500, Math.Abs(target - _translate.X) / PixelsPerSecond * 1000);
        _tween.Start(_translate.X, target, duration, new CubicEaseInOut(),
            v => _translate.X = v,
            onComplete: () =>
            {
                _towardEnd = !_towardEnd;
                if (IsActive) _hold.Start();
            });
    }

    private void ResetPan()
    {
        _tween.Stop();
        _hold.Stop();
        _translate.X = 0;
        _towardEnd = true;
    }
}
