using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A lightweight control that plays an animated GIF/WebP from a file path (decoded by
/// <see cref="GifDecoder"/>), cycling frames on a timer and drawing the current frame uniformly
/// scaled. Falls back to a single still frame for non-animated files.
/// </summary>
public sealed class AnimatedImageControl : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedImageControl, string?>(nameof(Source));

    public static readonly DirectProperty<AnimatedImageControl, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<AnimatedImageControl, bool>(nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<AnimatedImageControl, string?> LoadedSizeTextProperty =
        AvaloniaProperty.RegisterDirect<AnimatedImageControl, string?>(nameof(LoadedSizeText), o => o.LoadedSizeText);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private bool _isLoading;

    /// <summary>True while frames are being decoded (for a loading indicator).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    private string? _loadedSizeText;

    /// <summary>Human-readable decoded size of the loaded animation (its in-memory footprint), or null.</summary>
    public string? LoadedSizeText
    {
        get => _loadedSizeText;
        private set => SetAndRaise(LoadedSizeTextProperty, ref _loadedSizeText, value);
    }

    private GifAnimation? _animation;
    private string? _leasedPath; // the path we currently hold a cache lease on
    private int _index;
    private DispatcherTimer? _timer;
    private int _loadId; // monotonic: only the latest load may apply its result

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            _ = LoadAsync(Source);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loadId++;       // cancel any in-flight load
        StopAndClear();  // stop the timer and return the lease so frames can be freed
    }

    private async Task LoadAsync(string? path)
    {
        // Each call supersedes earlier ones; rapid hover fires many of these concurrently.
        var id = ++_loadId;
        StopAndClear();
        InvalidateVisual();

        if (string.IsNullOrEmpty(path)) { IsLoading = false; return; }

        // If something is already displaying this GIF, share its decoded frames (no re-decode).
        if (GifFrameCache.TryAcquire(path) is { } cached)
        {
            if (id != _loadId) { GifFrameCache.Release(path); return; } // superseded already
            IsLoading = false;
            Apply(path, cached);
            return;
        }

        IsLoading = true;
        var animation = await Task.Run(() => GifFrameCache.Acquire(path));
        if (id != _loadId)
        {
            if (animation is not null) GifFrameCache.Release(path); // superseded; don't hold this lease
            return;
        }

        IsLoading = false;
        Apply(path, animation);
    }

    private void Apply(string path, GifAnimation? animation)
    {
        StopAndClear(); // defensively drop any prior timer/lease before taking the new one
        _animation = animation;
        _leasedPath = animation is not null ? path : null; // released in StopAndClear
        LoadedSizeText = animation is null ? null : ByteFormat.Format(animation.Bytes);
        _index = 0;
        InvalidateMeasure();
        InvalidateVisual();
        if (animation is { Frames.Count: > 1 })
            StartTimer();
    }

    private void StopAndClear()
    {
        Stop();
        if (_leasedPath is not null)
        {
            GifFrameCache.Release(_leasedPath);
            _leasedPath = null;
        }
        _animation = null;
        LoadedSizeText = null;
        _index = 0;
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            if (_animation is null) return;
            _index = (_index + 1) % _animation.Frames.Count;
            _timer!.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _animation.DelaysMs[_index]));
            InvalidateVisual();
        };
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _animation!.DelaysMs[0]));
        _timer.Start();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private Bitmap? CurrentFrame =>
        _animation is { Frames.Count: > 0 } a ? a.Frames[Math.Min(_index, a.Frames.Count - 1)] : null;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (CurrentFrame is not { } frame) return;
        var dest = UniformRect(frame.Size, Bounds.Size);
        context.DrawImage(frame, new Rect(frame.Size), dest);
    }

    protected override Size MeasureOverride(Size availableSize)
        => CurrentFrame is { } frame ? UniformSize(frame.Size, availableSize) : default;

    private static Size UniformSize(Size content, Size available)
    {
        if (content.Width <= 0 || content.Height <= 0) return default;
        var scaleX = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / content.Width;
        var scaleY = double.IsInfinity(available.Height) ? double.PositiveInfinity : available.Height / content.Height;
        var scale = Math.Min(scaleX, scaleY);
        if (double.IsInfinity(scale)) scale = 1; // unconstrained → natural size
        return new Size(content.Width * scale, content.Height * scale);
    }

    private static Rect UniformRect(Size content, Size bounds)
    {
        var size = UniformSize(content, bounds);
        var x = (bounds.Width - size.Width) / 2;
        var y = (bounds.Height - size.Height) / 2;
        return new Rect(x, y, size.Width, size.Height);
    }
}
