using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Where a board screen points: a specific board, or the whole project ("All images"), optionally
/// pre-filtered by a search query (used for project-wide search results).</summary>
public sealed record BoardTarget(int? CollectionId, string Title, string? Search = null);

/// <summary>
/// The Board screen: a masonry grid of one board's images (or the whole project for "All images" / search
/// results), shown as <see cref="Controls.ItemCard"/>s. Owns the on-screen GIF playback (bounded LRU so memory
/// tracks what's visible), the detail panel, and per-asset delete/restore. Pushed above the Library screen;
/// the back chevron pops back.
/// </summary>
public partial class BoardViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly IngestService _ingest;
    private readonly ThumbnailCache? _thumbnails;
    private readonly ToastService _toasts;
    private readonly ImportStatus _importStatus;
    private readonly int? _collectionId;
    private readonly Action _requestBack;
    private CancellationTokenSource? _searchDebounce;

    // GIFs the user has tapped keep autoplaying in the grid; bounded (LRU) so memory stays sane.
    private const int MaxPlayingGifs = 12;
    private readonly LinkedList<AssetTileViewModel> _playing = new();

    public BoardViewModel(
        LibraryService library, CurationService curation, IngestService ingest,
        ThumbnailCache? thumbnails, ToastService toasts, ImportStatus importStatus,
        int? collectionId, string title, Action requestBack, string? initialSearch = null)
    {
        _library = library;
        _curation = curation;
        _ingest = ingest;
        _thumbnails = thumbnails;
        _toasts = toasts;
        _importStatus = importStatus;
        _collectionId = collectionId;
        Title = title;
        _requestBack = requestBack;
        _searchQuery = initialSearch ?? "";

        _importStatus.PropertyChanged += OnImportStatusChanged;
        UpdateImportState();
        _ = LoadAssetsAsync();
    }

    // Design-time constructor for the XAML previewer.
    public BoardViewModel() : this(null!, null!, null!, null, new ToastService(), new ImportStatus(), null, "All images", () => { }) { }

    public string Title { get; }
    public ObservableCollection<AssetTileViewModel> Assets { get; } = new();

    [ObservableProperty] private AssetTileViewModel? _selectedAsset;
    [ObservableProperty] private AssetDetailViewModel? _details;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isBusy;

    // Mirrors the shared import state, but only while THIS board is the import target (or this is All images).
    [ObservableProperty] private bool _isBoardImporting;
    [ObservableProperty] private string _importStatusText = "";

    /// <summary>True when the running import is landing in this board (a specific target, or any import for the
    /// virtual "All images" board).</summary>
    private bool ImportTargetsThisBoard =>
        _importStatus.IsImporting && (_collectionId is null || _importStatus.CollectionId == _collectionId);

    private void OnImportStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportStatus.LastImported))
        {
            AppendImportedIfRelevant();
            return;
        }
        var wasImporting = IsBoardImporting;
        UpdateImportState();
        // An import into this board just finished → reload to get the correct order, counts, and any filter.
        if (wasImporting && !IsBoardImporting) _ = LoadAssetsAsync();
    }

    private void UpdateImportState()
    {
        IsBoardImporting = ImportTargetsThisBoard;
        ImportStatusText = ImportTargetsThisBoard ? _importStatus.Text : "";
    }

    // Append a freshly-imported pin into the open board live (skip if filtered, or already shown).
    private void AppendImportedIfRelevant()
    {
        if (!ImportTargetsThisBoard || !string.IsNullOrEmpty(SearchQuery)) return;
        if (_importStatus.LastImported is not { } a || Assets.Any(t => t.Model.Id == a.Id)) return;
        Assets.Insert(0, NewTile(a)); // newest pin first (final order reconciled on the post-import reload)
    }

    public void Dispose() => _importStatus.PropertyChanged -= OnImportStatusChanged;

    [RelayCommand]
    private void Back() => _requestBack();

    [RelayCommand]
    private void CloseDetails() => SelectedAsset = null;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    // Load full metadata for the detail panel whenever the selected tile changes.
    partial void OnSelectedAssetChanged(AssetTileViewModel? value) => _ = LoadDetailsAsync(value);

    // Reload after a short pause so we don't query on every keystroke.
    partial void OnSearchQueryChanged(string value) => _ = DebouncedSearchAsync();

    private async Task LoadDetailsAsync(AssetTileViewModel? tile)
    {
        if (_library is null || tile is null) { Details = null; return; }
        var detail = await _library.GetAssetDetailAsync(tile.Model.Id);
        if (ReferenceEquals(SelectedAsset, tile)) // ignore if selection changed while loading
            Details = detail is null ? null : new AssetDetailViewModel(detail);
    }

    private Task ReloadDetailsAsync() => LoadDetailsAsync(SelectedAsset);

    /// <summary>Tapping a tile selects it (opens the detail panel) and, for a GIF, starts it autoplaying in
    /// the grid. It keeps playing after the detail panel is closed, until pushed out by newer plays.</summary>
    public void ActivateTile(AssetTileViewModel tile)
    {
        SelectedAsset = tile;
        if (!tile.IsGif) return;

        var existing = _playing.Find(tile);
        if (existing is not null) { _playing.Remove(existing); _playing.AddFirst(existing); return; }

        tile.IsPlaying = true;
        _playing.AddFirst(tile);
        while (_playing.Count > MaxPlayingGifs)
        {
            var oldest = _playing.Last!.Value;
            _playing.RemoveLast();
            oldest.IsPlaying = false; // stop the least-recently-played GIF
        }
    }

    /// <summary>Manually unload a playing GIF: stop it and (if it's the selected one) close the detail panel,
    /// so both drop their cache leases and the refcounted cache frees the frames.</summary>
    public void UnloadGif(AssetTileViewModel tile)
    {
        tile.IsPlaying = false;
        var node = _playing.Find(tile);
        if (node is not null) _playing.Remove(node);
        if (ReferenceEquals(SelectedAsset, tile)) SelectedAsset = null;
    }

    /// <summary>Tombstone the selected asset with a note: its blob is freed and a Remove op logged, but the
    /// tile stays in place (now showing the note) and can be restored. Updated in place so scroll survives.</summary>
    public async Task DeleteSelectedAsync(string note)
    {
        if (_curation is null || SelectedAsset is not { } tile || tile.IsDeleted) return;

        var sha = tile.Model.Sha256;
        try
        {
            if (await _curation.DeleteAssetAsync(tile.Model.Id, note) is null) return; // already gone
        }
        catch (Exception ex)
        {
            _toasts.Show($"Delete failed: {ex.Message}", isError: true);
            return;
        }

        var node = _playing.Find(tile);
        if (node is not null) _playing.Remove(node);
        _thumbnails?.Evict(sha);

        var title = tile.Model.Title ?? "image";
        tile.ApplyUpdate(tile.Model with { IsDeleted = true, DeletionNote = note });
        await ReloadDetailsAsync();
        _toasts.Show($"Deleted “{title}” — kept as a restorable tombstone.");
    }

    /// <summary>Restore the selected tombstone by re-downloading its original media, then show it live again.</summary>
    public async Task RestoreSelectedAsync()
    {
        if (_ingest is null || SelectedAsset is not { IsDeleted: true } tile) return;

        IsBusy = true;
        AssetView? restored;
        try
        {
            restored = await _ingest.RestoreAsync(tile.Model.Id);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Restore failed: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (restored is null) { _toasts.Show("Couldn't restore — the item may no longer exist at the source.", isError: true); return; }

        tile.ApplyUpdate(restored);      // back to live in place
        _ = tile.EnsureThumbnailAsync(); // decode the freshly re-fetched blob
        await ReloadDetailsAsync();
        _toasts.Show($"Restored “{restored.Title ?? "image"}”.");
    }

    private async Task DebouncedSearchAsync()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchDebounce, cts)?.Cancel();
        try { await Task.Delay(220, cts.Token); }
        catch (TaskCanceledException) { return; }
        await LoadAssetsAsync();
    }

    private async Task LoadAssetsAsync()
    {
        if (_library is null) return;
        var views = await _library.GetAssetsAsync(_collectionId, SearchQuery);
        // Tiles are being recreated: close the detail panel and forget what was playing so their GIF cache
        // leases drop and the frames free (memory tracks what's actually on screen).
        SelectedAsset = null;
        _playing.Clear();
        Assets.Clear();
        foreach (var v in views)
            Assets.Add(NewTile(v));
    }

    private AssetTileViewModel NewTile(AssetView v) => new(v, _thumbnails, ActivateTile, UnloadGif);
}
