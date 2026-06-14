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

    public AssetView Model { get; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isThumbnailLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaySource))]
    [NotifyPropertyChangedFor(nameof(ShowPlay))]
    private bool _isPlaying;

    public AssetTileViewModel(AssetView model, ThumbnailCache? cache = null)
    {
        Model = model;
        _cache = cache;
    }

    public string? Title => Model.Title;
    public bool IsImage => Model.Kind is MediaKind.Image or MediaKind.Gif;
    public bool IsGif => Model.Kind is MediaKind.Gif;
    public string KindLabel => Model.Kind.ToString();

    /// <summary>The GIF to animate while this tile is "playing" (null otherwise). Set by clicking the tile.</summary>
    public string? PlaySource => IsPlaying && IsGif ? Model.AbsolutePath : null;
    public bool ShowPlay => IsPlaying && IsGif;

    /// <summary>Height ÷ width, from the source metadata; 1 (square) when dimensions are unknown.</summary>
    public double AspectRatio =>
        Model is { Width: > 0, Height: > 0 } ? (double)Model.Height!.Value / Model.Width!.Value : 1.0;

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested) return;
        _thumbnailRequested = true;
        if (!IsImage) return;

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
