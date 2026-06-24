using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Hoard.Desktop.Controls;

/// <summary>
/// A media tile in the masonry grid: the image (or animated GIF) fills the card edge-to-edge under a convex
/// bevel + thin border (flat at rest, lifting a drop shadow on hover with expand-on-hover / shrink-on-press
/// feedback). The body tap opens the item (<see cref="OpenCommand"/>); a floating GIF/VIDEO tag and (while a
/// GIF plays) a memory-footprint badge + Unload button (<see cref="UnloadCommand"/>) sit over the media. A
/// deleted blob shows a recessed tombstone instead.
/// </summary>
public partial class ItemCard : UserControl
{
    public static readonly StyledProperty<Bitmap?> ThumbnailProperty =
        AvaloniaProperty.Register<ItemCard, Bitmap?>(nameof(Thumbnail));

    /// <summary>Non-null while this tile is "playing": drives the internal GIF control (and its visibility).</summary>
    public static readonly StyledProperty<string?> PlaySourceProperty =
        AvaloniaProperty.Register<ItemCard, string?>(nameof(PlaySource));

    public static readonly StyledProperty<bool> IsImageProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(IsImage), defaultValue: true);

    /// <summary>Centred label for a non-image body (e.g. "Video").</summary>
    public static readonly StyledProperty<string?> KindLabelProperty =
        AvaloniaProperty.Register<ItemCard, string?>(nameof(KindLabel));

    /// <summary>Show the floating GIF tag (a GIF — itself an image kind, so it needs an explicit flag; the
    /// VIDEO tag derives from <see cref="IsImage"/> instead). The deleted tombstone covers it when relevant.</summary>
    public static readonly StyledProperty<bool> ShowGifTagProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(ShowGifTag));

    public static readonly StyledProperty<bool> IsDeletedProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(IsDeleted));

    public static readonly StyledProperty<string?> DeletionNoteProperty =
        AvaloniaProperty.Register<ItemCard, string?>(nameof(DeletionNote));

    public static readonly StyledProperty<bool> IsThumbnailLoadingProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(IsThumbnailLoading));

    /// <summary>The asset is live but its blob is gone from the store — show the "file missing" + re-download state.</summary>
    public static readonly StyledProperty<bool> IsFileMissingProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(IsFileMissing));

    public static readonly StyledProperty<bool> IsRefetchingProperty =
        AvaloniaProperty.Register<ItemCard, bool>(nameof(IsRefetching));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<ItemCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> UnloadCommandProperty =
        AvaloniaProperty.Register<ItemCard, ICommand?>(nameof(UnloadCommand));

    public static readonly StyledProperty<ICommand?> RefetchCommandProperty =
        AvaloniaProperty.Register<ItemCard, ICommand?>(nameof(RefetchCommand));

    public Bitmap? Thumbnail { get => GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    public string? PlaySource { get => GetValue(PlaySourceProperty); set => SetValue(PlaySourceProperty, value); }
    public bool IsImage { get => GetValue(IsImageProperty); set => SetValue(IsImageProperty, value); }
    public string? KindLabel { get => GetValue(KindLabelProperty); set => SetValue(KindLabelProperty, value); }
    public bool ShowGifTag { get => GetValue(ShowGifTagProperty); set => SetValue(ShowGifTagProperty, value); }
    public bool IsDeleted { get => GetValue(IsDeletedProperty); set => SetValue(IsDeletedProperty, value); }
    public string? DeletionNote { get => GetValue(DeletionNoteProperty); set => SetValue(DeletionNoteProperty, value); }
    public bool IsThumbnailLoading { get => GetValue(IsThumbnailLoadingProperty); set => SetValue(IsThumbnailLoadingProperty, value); }
    public bool IsFileMissing { get => GetValue(IsFileMissingProperty); set => SetValue(IsFileMissingProperty, value); }
    public bool IsRefetching { get => GetValue(IsRefetchingProperty); set => SetValue(IsRefetchingProperty, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? UnloadCommand { get => GetValue(UnloadCommandProperty); set => SetValue(UnloadCommandProperty, value); }
    public ICommand? RefetchCommand { get => GetValue(RefetchCommandProperty); set => SetValue(RefetchCommandProperty, value); }

    public ItemCard()
    {
        InitializeComponent();
    }

    // True while the in-progress press is the primary (left) button; the press origin tells a tap from a drag.
    private bool _primaryPressed;
    private Point _pressOrigin;

    // Activate on RELEASE with pointer CAPTURE — NOT on the Tapped gesture. Logging proved the old bug: Avalonia's
    // Tapped needs the press AND release to hit the SAME visual, but a tile whose centre is off-screen (top/bottom
    // protruding past the viewport) scales toward that centre on press (the hover/press RenderTransform), shifting
    // it out from under the pointer — so the release hits a different visual, Tapped never fires, and the tile
    // "refuses to open". Capturing the pointer on press guarantees the release comes back to this card. The Unload
    // button handles its own pointer events (marks them handled), so this never fires for a press on it.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        _primaryPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (!_primaryPressed) return;
        _pressOrigin = e.GetPosition(this);
        SetPressed(true);
        e.Pointer.Capture(CardRoot);
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasPrimary = _primaryPressed;
        _primaryPressed = false;
        SetPressed(false);
        if (ReferenceEquals(e.Pointer.Captured, CardRoot)) e.Pointer.Capture(null);

        if (!wasPrimary || e.InitialPressMouseButton != MouseButton.Left) return;
        // Ignore a release over an inner button (e.g. Unload), and a drag (released far from where it was pressed).
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        var moved = e.GetPosition(this) - _pressOrigin;
        if (moved.X * moved.X + moved.Y * moved.Y > 12 * 12) return;

        if (OpenCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // A drag past the tap threshold (e.g. a touch/pen scroll started on the tile) isn't a tap: release the capture
    // so the ScrollViewer can take the gesture over, and cancel the pending activation. e.GetPosition(this) is in
    // the card's own (untransformed) space, so the press-scale never trips this and a click-in-place stays a tap.
    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (!_primaryPressed) return;
        var moved = e.GetPosition(this) - _pressOrigin;
        if (moved.X * moved.X + moved.Y * moved.Y <= 12 * 12) return;
        _primaryPressed = false;
        SetPressed(false);
        if (ReferenceEquals(e.Pointer.Captured, CardRoot)) e.Pointer.Capture(null);
    }

    private void OnCardExited(object? sender, PointerEventArgs e) => SetPressed(false);
    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _primaryPressed = false;
        SetPressed(false);
    }

    private void SetPressed(bool value)
    {
        if (value)
        {
            if (!CardRoot.Classes.Contains("pressed")) CardRoot.Classes.Add("pressed");
        }
        else
        {
            CardRoot.Classes.Remove("pressed");
        }
    }
}
