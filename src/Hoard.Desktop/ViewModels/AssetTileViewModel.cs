using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
public partial class AssetTileViewModel : ViewModelBase, IMasonryItem, IDisposable
{
    private readonly ThumbnailCache? _cache;
    private bool _thumbnailRequested;

    public AssetView Model { get; private set; }

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isThumbnailLoading;

    /// <summary>The blob is gone from the store (deleted/moved outside the app) though the asset is live — the
    /// tile shows a "file missing" state with a re-download action.</summary>
    [ObservableProperty] private bool _isFileMissing;
    [ObservableProperty] private bool _isRefetching;

    /// <summary>This tile is the one expanded inline into the full-width detail band (the grid packs around it).
    /// The small tile chrome hides while expanded; GIF playback is independent of this (a played GIF keeps
    /// animating in its tile whether or not it's expanded).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandPlaySource))]
    [NotifyPropertyChangedFor(nameof(BandContent))]
    private bool _isExpanded;

    /// <summary>This tile while it is the expanded one, otherwise null. The inline detail band's <c>ContentControl</c>
    /// binds its <c>Content</c> here so Avalonia instantiates the (heavy) band markup — rail, actions, big media —
    /// ONLY for the expanded tile, never building it into every realized/recycled tile. Building the band into every
    /// tile was the bulk of a detached view's retained memory (a leaked view kept ~150 fully-built bands).</summary>
    public AssetTileViewModel? BandContent => IsExpanded ? this : null;

    /// <summary>The GIF path for the inline detail band to animate — non-null ONLY when this tile is the expanded
    /// one (and is a GIF). The band's <see cref="AnimatedImageControl"/> loads on its <c>Source</c> regardless of
    /// visibility, and the band markup is instantiated in every realized tile, so binding it to the shared detail's
    /// path would decode/play the selected GIF in every collapsed tile too. Gating on <see cref="IsExpanded"/>
    /// keeps it to the one tile actually showing it.</summary>
    public string? BandPlaySource => IsExpanded && IsGif && !IsDeleted ? Model.AbsolutePath : null;

    /// <summary>Opacity of the expanded band's content (0 while the masonry is reflowing to make room, 1 once it
    /// has settled), so the band fades in only after the tiles finish moving — and out before they reflow back.
    /// The band Border tweens this via a transition; the view sets the target. -1 is never used; default 0.</summary>
    [ObservableProperty] private double _bandContentOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaySource))]
    private bool _isPlaying;

    /// <summary>Tap the tile (the <see cref="ItemCard"/> body): open/activate it (and play, for a GIF).</summary>
    public IRelayCommand OpenCommand { get; }
    /// <summary>The tile's Unload button: stop + free a playing GIF.</summary>
    public IRelayCommand UnloadCommand { get; }
    /// <summary>The "file missing" state's Re-download button.</summary>
    public IRelayCommand RefetchCommand { get; }

    // The board-method callbacks are held as FIELDS (read by the command closures at invoke time) rather than
    // captured directly in the closures, so Dispose can null them. Avalonia's ItemsRepeater caches its last
    // ElementClearing/Prepared event-args, which pins the last-cleared ItemCard container — and through it this
    // tile. If the tile still referenced the board here (onOpen/onUnload/onRefetch are BoardViewModel methods, so
    // their delegates' Target IS the board), that single framework-retained tile would keep the whole board (and
    // its thousands of sibling tiles + AssetViews) alive. This was the board memory leak.
    private Action<AssetTileViewModel>? _onOpen;
    private Action<AssetTileViewModel>? _onUnload;
    private Action<AssetTileViewModel>? _onRefetch;

    public AssetTileViewModel(
        AssetView model, ThumbnailCache? cache = null,
        Action<AssetTileViewModel>? onOpen = null, Action<AssetTileViewModel>? onUnload = null,
        Action<AssetTileViewModel>? onRefetch = null)
    {
        Model = model;
        _cache = cache;
        _onOpen = onOpen;
        _onUnload = onUnload;
        _onRefetch = onRefetch;
        OpenCommand = new RelayCommand(() => _onOpen?.Invoke(this));
        UnloadCommand = new RelayCommand(() => _onUnload?.Invoke(this));
        RefetchCommand = new RelayCommand(() => _onRefetch?.Invoke(this));
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
        Thumbnail = null;            // dispose the old bitmap (via OnThumbnailChanged) and clear the Image
        IsFileMissing = false;       // a re-fetch/restore may have put the blob back
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
        if (IsDeleted) return; // a tombstone shows its note, not media

        // Lazy per-tile existence check: a live asset whose blob has vanished from the store (deleted/moved
        // outside the app) shows a "file missing" state with a re-download, instead of a blank placeholder. Probed
        // off the realising thread (this runs as the masonry realises a container) so a slow drive can't jank
        // scroll; the continuation resumes on the UI thread to set the bound state.
        var path = Model.AbsolutePath;
        if (!await Task.Run(() => System.IO.File.Exists(path))) { IsFileMissing = true; return; }
        if (!IsImage) return; // video/other: no thumbnail, but the file is present

        IsThumbnailLoading = true;
        try
        {
            var decoded = _cache is not null
                ? await _cache.GetAsync(Model.Sha256, Model.AbsolutePath)
                : await DecodeDirectAsync();
            // The container may have been recycled (ReleaseThumbnail cleared _thumbnailRequested) while we decoded.
            // Retaining the bitmap now would orphan it — no ElementClearing fires for an already-recycled tile — so
            // drop it; a later realize re-decodes from the on-disk cache. This is the fast-scroll leak.
            if (!_thumbnailRequested) { decoded?.Dispose(); return; }
            Thumbnail = decoded;
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
            return Bitmap.DecodeToWidth(stream, ThumbnailCache.Width);
        });
    }

    // ── Native bitmap lifetime ────────────────────────────────────────────────────
    // The thumbnail is an Avalonia Bitmap = an unmanaged (Skia) surface; the managed wrapper is tiny, so the GC
    // feels no pressure and dropping the reference alone leaves the real memory to lag in finalization. Free the
    // PREVIOUS bitmap eagerly whenever Thumbnail is swapped — recycle, ApplyUpdate, Dispose — so memory tracks
    // what's near the viewport instead of climbing with every image ever shown.
    partial void OnThumbnailChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (oldValue is null) return;
        // Dispose synchronously. Deferring to a Background dispatcher tick STARVED disposal during a fast scroll:
        // released bitmaps piled up un-freed (1000+ live, working set ballooning) until the scroll stopped, and the
        // native heap kept that high-water mark. Avalonia's deferred renderer ref-counts the bitmap impl per composed
        // frame, so freeing the managed wrapper here (the Image rebinds to the new value on the PropertyChanged that
        // immediately follows) can't free a surface mid-draw.
        oldValue.Dispose();
    }

    /// <summary>Drop the decoded thumbnail (freeing its native memory) when the tile's container is recycled
    /// off-screen, so a long scroll doesn't retain a bitmap per image. Re-realizing the container re-decodes from
    /// the fast on-disk cache. No-op for a tile holding nothing decoded (tombstone / missing / not yet loaded).</summary>
    public void ReleaseThumbnail()
    {
        if (Thumbnail is null) return;
        Thumbnail = null;            // → OnThumbnailChanged frees the native bitmap
        _thumbnailRequested = false; // allow a fresh decode when the container is realized again
    }

    /// <summary>Free the thumbnail's native memory and drop the board-method callbacks when the board is left (the VM
    /// disposed). Nulling the callbacks is what actually breaks the leak: a tile that Avalonia's ItemsRepeater still
    /// holds (via its cached element event-args) then references nothing heavy, so it can't keep the board alive.</summary>
    public void Dispose()
    {
        Thumbnail = null; // → OnThumbnailChanged frees the native bitmap
        // Mark unrequested so an EnsureThumbnailAsync decode still in flight when the board is left (its guard reads
        // this) drops its result instead of assigning a fresh bitmap onto this dead tile — which would re-pin the
        // native surface with no later swap to free it.
        _thumbnailRequested = false;
        _onOpen = null;
        _onUnload = null;
        _onRefetch = null;
    }
}
