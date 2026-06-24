using System;
using System.Diagnostics;
using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace Hoard.Desktop.Infrastructure;

/// <summary>
/// A tiny UI-thread value tween: drives a number from <c>from</c> to <c>to</c> over a duration on a ~60fps
/// <see cref="DispatcherTimer"/>, calling <c>onStep</c> with the eased value each frame and <c>onComplete</c> at
/// the end. Avalonia's animation system can't target a plain field (a layout's lerp factor, a ScrollViewer's
/// offset), so the masonry reflow and the scroll-to-top both use this rather than each hand-rolling a
/// timer+stopwatch loop. Reusable: call <see cref="Start"/> again to retarget, <see cref="Stop"/> to cancel
/// (e.g. on view detach so the timer doesn't tick on a dead control).
/// </summary>
internal sealed class Tween
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _from, _to, _durationMs;
    private long _startMs;
    private IEasing _easing = new LinearEasing();
    private Action<double>? _onStep;
    private Action? _onComplete;

    public Tween()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    /// <summary>(Re)start a tween, replacing any in-flight one. A zero-length move (from ≈ to) or non-positive
    /// duration completes synchronously (onStep(to) then onComplete) so callers get a single, predictable finish.</summary>
    public void Start(double from, double to, double durationMs, IEasing easing,
        Action<double> onStep, Action? onComplete = null)
    {
        _from = from;
        _to = to;
        _durationMs = durationMs;
        _easing = easing;
        _onStep = onStep;
        _onComplete = onComplete;

        if (Math.Abs(from - to) < 0.0001 || durationMs <= 0)
        {
            _timer.Stop();
            onStep(to);
            onComplete?.Invoke();
            return;
        }

        _startMs = _clock.ElapsedMilliseconds;
        _timer.Start();
    }

    /// <summary>Cancel an in-flight tween without firing onComplete (leaves the value wherever it reached).</summary>
    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var t = (_clock.ElapsedMilliseconds - _startMs) / _durationMs;
        if (t >= 1)
        {
            _timer.Stop();
            _onStep?.Invoke(_to);
            _onComplete?.Invoke();
            return;
        }
        _onStep?.Invoke(_from + (_to - _from) * _easing.Ease(t));
    }
}
