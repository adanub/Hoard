using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hoard.Desktop.Infrastructure;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A fullscreen zoom/pan image viewer overlaid on the page. Scroll-wheel zooms anchored at the cursor, drag
/// pans, double-click resets to fit; clicking the scrim (the margin around the image), the ✕, or Esc closes it
/// (raising <see cref="CloseCommand"/>). It shows a still <see cref="Bitmap"/> (decoded full-resolution from the
/// path, on demand) or an animated <see cref="AnimatedImageControl"/>, chosen by <see cref="IsGif"/>. The control
/// is always in the tree but hidden while <see cref="IsOpen"/> is false; its media is loaded on open and freed on
/// close so memory tracks only what's being viewed.
///
/// Zoom/pan is a single accumulating affine <see cref="Matrix"/> applied as the media host's RenderTransform —
/// set directly (never a code-built RenderTransform <c>Animation</c>, which throws on a deferred timer per the
/// CLAUDE.md gotcha). Anchored zoom keeps the cursor's point fixed: M' = M · T(-c) · S · T(c); pan is M · T(d).
/// </summary>
public partial class Lightbox : UserControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Lightbox, bool>(nameof(IsOpen));

    /// <summary>The media file path to view (full-resolution). Null shows nothing.</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<Lightbox, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsGifProperty =
        AvaloniaProperty.Register<Lightbox, bool>(nameof(IsGif));

    /// <summary>Raised when the user dismisses the viewer (scrim / ✕ / Esc).</summary>
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<Lightbox, ICommand?>(nameof(CloseCommand));

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsGif { get => GetValue(IsGifProperty); set => SetValue(IsGifProperty, value); }
    public ICommand? CloseCommand { get => GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    private const double MinScale = 1.0;   // fit-to-viewport floor (don't shrink below fit)
    private const double MaxScale = 8.0;
    private const double ZoomStep = 1.15;

    private readonly MatrixTransform _transform = new(Matrix.Identity);
    private Matrix _matrix = Matrix.Identity;
    private Bitmap? _bitmap;
    private int _loadId;           // monotonic: only the latest decode may apply its result
    private bool _panning;
    private bool _lastPressPrimary; // gates DoubleTapped, which fires for every pointer button (the Tapped gotcha)
    private Point _lastPan;

    public Lightbox()
    {
        InitializeComponent();
        ZoomHost.RenderTransform = _transform;

        Scrim.PointerPressed += OnScrimPressed;
        Scrim.PointerWheelChanged += (_, e) => e.Handled = true; // don't scroll the board behind the open viewer
        CloseButton.Click += (_, _) => Close();

        Viewport.PointerWheelChanged += OnWheel;
        Viewport.PointerPressed += OnViewportPressed;
        Viewport.PointerMoved += OnViewportMoved;
        Viewport.PointerReleased += OnViewportReleased;
        // Capture can be lost without a PointerReleased — e.g. the lightbox is hidden (Esc/✕/reload) mid-drag, or
        // the window deactivates. Clear the pan flag so a reopen doesn't "sticky-pan" on the first hover move.
        Viewport.PointerCaptureLost += (_, _) => _panning = false;
        // DoubleTapped fires for EVERY pointer button (the CLAUDE.md Tapped gotcha) — gate reset-to-fit on the last
        // press having been primary, so a double right/middle-click doesn't throw away the user's zoom+pan.
        Viewport.DoubleTapped += (_, _) => { if (_lastPressPrimary) ResetTransform(); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            IsVisible = IsOpen;
            // Reset on the way OUT as well as in: a viewer closed while zoomed used to keep its (possibly 8×,
            // heavily translated) matrix on ZoomHost until the next open. Nothing should be left holding a
            // scale on a hidden subtree — and "fullscreen misdraws after using the lightbox" is exactly the
            // sort of report that shouldn't have a stale transform anywhere near it.
            _panning = false;
            ResetTransform();
            if (IsOpen) LoadMedia(); else ClearMedia();
            LayoutProbe.Note($"lightbox {(IsOpen ? "opened" : "closed")}");
        }
        else if (change.Property == SourceProperty && IsOpen)
        {
            ResetTransform();
            LoadMedia();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearMedia();  // free media if the board was popped while the viewer was open
    }

    // The ✕ and the scrim raise CloseCommand (wired to the board's CloseLightbox, which steps back one navigation
    // step — the zoom is a history step). The lightbox does NOT handle Esc itself: Esc is a window-level unified
    // "back" (MainWindow), so it works regardless of where focus sits.
    private void Close()
    {
        if (CloseCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Scrim).Properties.IsLeftButtonPressed) Close();
    }

    // ── Media load / free ────────────────────────────────────────────────────────

    private async void LoadMedia()
    {
        ClearMedia();        // frees the current media AND bumps _loadId, superseding any still-in-flight decode
        var id = _loadId;

        var path = Source;
        if (string.IsNullOrEmpty(path)) return;

        // Everything that could throw lives in the try — this is an `async void` (a property-changed handler), so an
        // unhandled throw here would be unobserved and crash the process rather than degrading to "show nothing".
        try
        {
            if (IsGif)
            {
                // The AnimatedImageControl decodes + frees its own frames (lease) off its Source.
                Gif.IsVisible = true;
                StillImage.IsVisible = false;
                Gif.Source = path;
                return;
            }

            StillImage.IsVisible = true;
            Gif.IsVisible = false;
            Spinner.IsVisible = true;
            var bmp = await Task.Run(() => new Bitmap(path));
            if (id != _loadId || !IsOpen) { bmp.Dispose(); return; } // superseded or closed during decode
            _bitmap = bmp;
            StillImage.Source = bmp;
        }
        catch
        {
            // Missing/corrupt — just show nothing (the viewer still closes normally).
        }
        finally
        {
            if (id == _loadId) Spinner.IsVisible = false;
        }
    }

    private void ClearMedia()
    {
        // Supersede any in-flight still decode so a completion AFTER this point (a close, a source switch, or a
        // board Pop that detaches mid-decode) disposes its bitmap instead of assigning it onto a cleared/detached
        // control — mirrors AnimatedImageControl bumping its load id on detach.
        _loadId++;
        Gif.Source = null;          // releases the GIF lease so its frames free
        StillImage.Source = null;
        Spinner.IsVisible = false;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    // ── Zoom / pan ─────────────────────────────────────────────────────────────────
    // All anchoring is in Viewport coordinates; ZoomHost fills the Viewport with a top-left transform origin, so
    // its local space equals Viewport space at identity and the matrix maps between them.

    private void ApplyMatrix() => _transform.Matrix = _matrix;

    private void ResetTransform()
    {
        _matrix = Matrix.Identity;
        ApplyMatrix();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) { e.Handled = true; return; } // horizontal / shift-scroll is not a zoom gesture
        var step = e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep;
        var current = _matrix.M11;            // pure scale+translate ⇒ M11 == M22 == scale
        var proposed = current * step;

        if (proposed <= MinScale)
        {
            ResetTransform();                 // snapped back to fit ⇒ also recentre (drop any pan offset)
        }
        else
        {
            var effective = Math.Min(proposed, MaxScale) / current;
            var c = e.GetPosition(Viewport);
            _matrix = _matrix
                * Matrix.CreateTranslation(-c.X, -c.Y)
                * Matrix.CreateScale(effective, effective)
                * Matrix.CreateTranslation(c.X, c.Y);
            ApplyMatrix();
        }
        e.Handled = true;
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        // Pan at any zoom level, including fit — let the user grab and reposition the image freely; double-click
        // (or zooming back out to fit via the wheel) recentres it.
        _lastPressPrimary = e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed;
        if (!_lastPressPrimary) return;
        _panning = true;
        _lastPan = e.GetPosition(Viewport);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var pos = e.GetPosition(Viewport);
        var d = pos - _lastPan;
        _lastPan = pos;
        _matrix *= Matrix.CreateTranslation(d.X, d.Y);
        ApplyMatrix();
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        if (ReferenceEquals(e.Pointer.Captured, Viewport)) e.Pointer.Capture(null);
    }
}
