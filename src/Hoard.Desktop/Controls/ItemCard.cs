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

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        // Ignore taps that came from a button inside the tile (e.g. the Unload button): the Tapped gesture
        // bubbles up past the button's Click, and re-firing OpenCommand would re-activate/replay the tile.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (OpenCommand is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }

    // Press feedback toggles a "pressed" class on the card (:pointerover is automatic). The Unload button
    // handles its own pointer events, so pressing it never triggers the card's press/scale or its tap.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => SetPressed(true);
    private void OnCardReleased(object? sender, PointerReleasedEventArgs e) => SetPressed(false);
    private void OnCardExited(object? sender, PointerEventArgs e) => SetPressed(false);
    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => SetPressed(false);

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
