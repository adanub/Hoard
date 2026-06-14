using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Desktop.Controls;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>
/// One image in the grid. The thumbnail is loaded lazily (via <see cref="EnsureThumbnailAsync"/>,
/// triggered when the container is realized) from the per-project cache, so a library of thousands of
/// assets stays light and repeat views are fast.
/// </summary>
public partial class AssetTileViewModel : ViewModelBase, IMasonryItem
{
    private const int ThumbnailWidth = 256;
    private readonly ThumbnailCache? _cache;
    private bool _thumbnailRequested;

    public AssetView Model { get; private set; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isThumbnailLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaySource))]
    private bool _isPlaying;

    public AssetTileViewModel(AssetView model, ThumbnailCache? cache = null)
    {
        Model = model;
        _cache = cache;
    }

    /// <summary>
    /// Swap this tile's model in place (e.g. after a delete/restore) and refresh its bindings. The held
    /// thumbnail bitmap is dropped so the on-screen container updates immediately — rather than relying on
    /// the repeater to re-realize a replaced item, which only happens on the next recycle (scroll/nav away).
    /// </summary>
    public void ApplyUpdate(AssetView updated)
    {
        Model = updated;
        IsPlaying = false;
        Thumbnail = null;            // release the bitmap and clear the Image
        _thumbnailRequested = false; // allow a fresh decode if it becomes live again (restore)
        OnPropertyChanged(string.Empty); // re-evaluate every derived binding (IsDeleted, IsGifBadgeVisible, …)
    }

    public bool IsImage => Model.Kind is MediaKind.Image or MediaKind.Gif;
    public bool IsGif => Model.Kind is MediaKind.Gif;
    public string KindLabel => Model.Kind.ToString();

    /// <summary>Tombstoned: the blob is gone, so the tile shows <see cref="DeletionNote"/> instead of media.</summary>
    public bool IsDeleted => Model.IsDeleted;
    public string? DeletionNote => Model.DeletionNote;

    /// <summary>Show the "GIF" badge only on a live GIF (a tombstone shows its note, not media chrome).</summary>
    public bool IsGifBadgeVisible => IsGif && !IsDeleted;

    /// <summary>The GIF to animate while this tile is "playing" (null otherwise). Set by clicking the tile.
    /// Non-null also drives the GIF control's visibility, so a separate flag isn't needed.</summary>
    public string? PlaySource => IsPlaying && IsGif ? Model.AbsolutePath : null;

    /// <summary>Height ÷ width, from the source metadata; 1 (square) when dimensions are unknown.</summary>
    public double AspectRatio =>
        Model is { Width: > 0, Height: > 0 } ? (double)Model.Height!.Value / Model.Width!.Value : 1.0;

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        if (!IsImage || IsDeleted) return; // a tombstone has no blob to decode

        IsThumbnailLoading = true;
        try
        {
            Thumbnail = _cache is not null
                ? await _cache.GetAsync(Model.Sha256, Model.AbsolutePath, ThumbnailWidth)
                : await DecodeDirectAsync();
        }
        catch
        {
            // Corrupt/unsupported file — leave the placeholder showing.
            _thumbnailRequested = false;
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }

    // Fallback when no cache is available (design-time previewer / tests).
    private async Task<Bitmap> DecodeDirectAsync()
    {
        var path = Model.AbsolutePath;
        return await Task.Run(() =>
        {
            using var stream = System.IO.File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        });
    }
}
