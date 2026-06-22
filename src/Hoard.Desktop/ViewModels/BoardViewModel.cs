using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;
using Hoard.Ingest.GalleryDl;

namespace Hoard.Desktop.ViewModels;

/// <summary>Where a board screen points: a specific board, or the whole project ("All images"), optionally
/// pre-filtered by a search query (used for project-wide search results). <see cref="ParentId"/> is set when
/// drilling into a child folder (a Pinterest section / sub-folder), carrying the parent so the folder screen
/// can offer "move up" + edit-this-folder.</summary>
public sealed record BoardTarget(
    int? CollectionId, string Title, string? Search = null, int? ParentId = null, string? ParentTitle = null);

/// <summary>Marker for the leading "+ New folder" tile in the Board screen's folder row (own template).</summary>
public sealed class NewFolderTile : ViewModelBase
{
    public static readonly NewFolderTile Instance = new();
    private NewFolderTile() { }
}

/// <summary>One destination the selected pin can be filed to: a child folder of the board, or (CollectionId
/// null) "up" to the parent board when viewing inside a folder.</summary>
public sealed record MoveTarget(string Display, int? CollectionId);

/// <summary>
/// The Board screen: a masonry grid of one board's images (or the whole project for "All images" / search
/// results), shown as <see cref="Controls.ItemCard"/>s. Owns the on-screen GIF playback (bounded LRU so memory
/// tracks what's visible), the detail panel, and per-asset delete/restore. Pushed above the Library screen;
/// the back chevron pops back.
/// </summary>
public partial class BoardViewModel : ViewModelBase, IDisposable, IResumable, IHasBack
{
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly IngestService _ingest;
    private readonly ThumbnailCache? _thumbnails;
    private readonly ToastService _toasts;
    private readonly ImportStatus _importStatus;
    private readonly ProjectManager? _projects;
    private readonly int? _collectionId;
    private readonly int? _parentId;
    private readonly string? _parentTitle;
    private readonly Action _requestBack;
    private readonly Action<BoardTarget> _openBoard;
    private CancellationTokenSource? _searchDebounce;
    // Cancelled when the board is navigated away from (disposed), so an in-flight re-download doesn't keep running
    // and mutate this dead VM / toast for a screen you've left.
    private readonly CancellationTokenSource _disposeCts = new();
    private IReadOnlyList<string> _sourceUrls = Array.Empty<string>();

    // GIFs the user has tapped keep autoplaying in the grid; bounded (LRU) so memory stays sane.
    private const int MaxPlayingGifs = 12;
    private readonly LinkedList<AssetTileViewModel> _playing = new();

    public BoardViewModel(
        LibraryService library, CurationService curation, IngestService ingest,
        ThumbnailCache? thumbnails, ToastService toasts, ImportStatus importStatus, ProjectManager? projects,
        BoardTarget target, Action requestBack, Action<BoardTarget> openBoard)
    {
        _library = library;
        _curation = curation;
        _ingest = ingest;
        _thumbnails = thumbnails;
        _toasts = toasts;
        _importStatus = importStatus;
        _projects = projects;
        _collectionId = target.CollectionId;
        _parentId = target.ParentId;
        _parentTitle = target.ParentTitle;
        _title = target.Title;
        _requestBack = requestBack;
        _openBoard = openBoard;
        _searchQuery = target.Search ?? "";
        FolderEditor = new BoardCardEditor(
            library, curation, thumbnails, toasts, "folder",
            removeCard: r => FolderTiles.Remove(r),
            afterChange: () => OnPropertyChanged(nameof(CanMoveSelected)));

        _importStatus.PropertyChanged += OnImportStatusChanged;
        UpdateImportState();
        _ = LoadAssetsAsync();
        _ = LoadSourcesAsync(); // for the Sync button (a real board with at least one URL'd source)
        _ = LoadFoldersAsync(); // child folders (Pinterest sections / sub-folders) shown above the grid
    }

    // Design-time constructor for the XAML previewer.
    public BoardViewModel() : this(null!, null!, null!, null, new ToastService(), new ImportStatus(), null, new BoardTarget(null, "All images"), () => { }, _ => { }) { }

    [ObservableProperty] private string _title;
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
        // An import into this board just finished → reload the grid (order/counts/filter) AND the folder row
        // (sections may have been created/grown).
        if (wasImporting && !IsBoardImporting)
        {
            _ = LoadAssetsAsync();
            _ = LoadFoldersAsync();
        }
    }

    private void UpdateImportState()
    {
        IsBoardImporting = ImportTargetsThisBoard;
        ImportStatusText = ImportTargetsThisBoard ? _importStatus.Text : "";
    }

    // File a freshly-imported pin into the open board live, in the SAME place the ingest actually put it — never
    // dumped on the root grid to be reorganised at the end. A loose pin (or any pin in the global "All images"
    // view) shows on this grid; a sectioned pin belongs to a child folder, so reflect it in the folder row
    // (debounced to coalesce a burst) and keep it OFF the root grid. (Skip if filtered, or already shown.)
    private void AppendImportedIfRelevant()
    {
        if (!ImportTargetsThisBoard || !string.IsNullOrEmpty(SearchQuery)) return;
        if (_importStatus.LastImported is not { } a) return;

        if (_collectionId is null || _importStatus.LastImportedCollectionId == _collectionId)
        {
            if (!Assets.Any(t => t.Model.Id == a.Id))
                Assets.Insert(0, NewTile(a)); // newest first (final order reconciled on the post-import reload)
        }
        else
        {
            _ = DebouncedFolderReloadAsync(); // landed in a child folder → update the folder row, not the grid
        }
    }

    private CancellationTokenSource? _folderReloadDebounce;

    // Coalesce a rapid burst of sectioned pins into one folder-row reload (counts/covers) so it stays live
    // without re-querying per pin.
    private async Task DebouncedFolderReloadAsync()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _folderReloadDebounce, cts)?.Cancel();
        try { await Task.Delay(400, cts.Token); }
        catch (TaskCanceledException) { return; }
        await LoadFoldersAsync();
    }

    public void Dispose()
    {
        _importStatus.PropertyChanged -= OnImportStatusChanged;
        // Cancel (not just Dispose) so a debounced search/folder-reload mid-Task.Delay bails instead of waking up
        // to query + rebuild on this disposed VM — Dispose() alone does NOT cancel a pending Task.Delay.
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _folderReloadDebounce?.Cancel();
        _folderReloadDebounce?.Dispose();
        _disposeCts.Cancel(); // abort any in-flight re-download before it touches this disposed VM
        _disposeCts.Dispose();
    }

    /// <summary>Revealed again after a drilled-into folder was popped: reload only the folder row (a folder may
    /// have been renamed/deleted/created, or its count changed). The grid is left untouched so the scroll
    /// position and any open detail panel survive — a pin moved out to this board reflects on the next genuine
    /// reload (import/sync/re-open) rather than resetting the view on every back.</summary>
    public void OnResumed() => _ = LoadFoldersAsync();

    // ── Folders (child boards / Pinterest sections), shown as board cards before the images ──

    /// <summary>The folder grid above the images: a leading "+ New folder" tile then one card per child folder,
    /// rendered exactly like a top-level board (collage cover, count, pencil edit). Empty for "All images" /
    /// search results (no parent to nest under).</summary>
    public ObservableCollection<ViewModelBase> FolderTiles { get; } = new();

    /// <summary>True when this screen can hold folders (a real board or a folder), so the folder grid shows.</summary>
    public bool ShowFolderRow => _collectionId is not null;

    /// <summary>True when this screen IS a child folder (drilled into) — lets a pin "move up" to the parent.</summary>
    public bool IsFolder => _parentId is not null;

    private async Task LoadFoldersAsync()
    {
        if (_library is null || _collectionId is not int id) return;

        // Build the new tiles first, then swap in one go — so a failed query (or the board being popped
        // mid-flight) never leaves FolderTiles cleared-but-not-repopulated. Fire-and-forget callers can't observe
        // a throw, so it's caught here.
        List<ViewModelBase> tiles;
        try
        {
            var children = await _library.GetChildBoardsAsync(id);
            tiles = new List<ViewModelBase> { NewFolderTile.Instance };
            foreach (var c in children)
            {
                var count = c.ItemCount == 1 ? "1 image" : $"{c.ItemCount} images";
                // A folder card opens the folder (drill-down) and edits via its pencil — same as a top-level board.
                tiles.Add(new BoardCardRef(c.Id, c.Name, DrillIntoFolder, FolderEditor.Begin)
                {
                    MetaText = $"{count} · {ByteFormat.Format(c.SizeBytes)}",
                });
            }
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't load folders: {ex.Message}", isError: true);
            return; // leave the existing folder row intact
        }

        FolderTiles.Clear();
        foreach (var t in tiles) FolderTiles.Add(t);
        OnPropertyChanged(nameof(CanMoveSelected));
        _ = LoadFolderCoversAsync();
    }

    private void DrillIntoFolder(BoardCardRef r)
        => _openBoard(new BoardTarget(r.CollectionId, r.Name, ParentId: _collectionId, ParentTitle: Title));

    // Load each folder card's 3-up collage covers (spread across the folder), same as the Library board cards.
    private Task LoadFolderCoversAsync()
        => BoardCardCovers.LoadAsync(FolderTiles.OfType<BoardCardRef>().ToArray(), _library!, _thumbnails);

    // ── New folder ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isNewFolderSheetOpen;
    [ObservableProperty] private string _newFolderName = "";

    [RelayCommand]
    private void OpenNewFolderSheet()
    {
        NewFolderName = "";
        IsNewFolderSheetOpen = true;
    }

    [RelayCommand]
    private void CloseNewFolderSheet() => IsNewFolderSheetOpen = false;

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        if (_ingest is null || _collectionId is not int parentId) return;
        var name = string.IsNullOrWhiteSpace(NewFolderName) ? "New folder" : NewFolderName.Trim();
        IsNewFolderSheetOpen = false;
        try
        {
            await _ingest.CreateBoardAsync(name, "", parentId: parentId);
            await LoadFoldersAsync();
            _toasts.Show($"Created folder “{name}”.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't create folder: {ex.Message}", isError: true); }
    }

    // ── Folder Edit popup (a folder card's pencil — the shared BoardCardEditor, same as the Library board edit) ──

    /// <summary>The folder pencil's Edit popup (rename / clear cache / delete); a folder is just a child board, so
    /// it reuses the same editor as the Library board cards. Constructed in the ctor with "folder" wording, its
    /// card removed from <see cref="FolderTiles"/> on delete, and re-evaluating <see cref="CanMoveSelected"/>.</summary>
    public BoardCardEditor FolderEditor { get; }

    // ── Move the selected pin into a folder (one at a time) ─────────────────────

    [ObservableProperty] private bool _isMoveSheetOpen;
    public ObservableCollection<MoveTarget> MoveTargets { get; } = new();
    [ObservableProperty] private MoveTarget? _selectedMoveTarget;

    /// <summary>The selected live pin can be filed somewhere when this board has folders, or this screen is a
    /// folder (so the pin can move back up to the parent board).</summary>
    public bool CanMoveSelected =>
        SelectedAsset is { IsDeleted: false } && (IsFolder || FolderTiles.OfType<BoardCardRef>().Any());

    /// <summary>Open the move picker for the selected pin: this board's folders (+ "move out" to the parent
    /// when inside a folder).</summary>
    public void OpenMoveSheet()
    {
        if (!CanMoveSelected) { _toasts.Show("Create a folder first to file images into it."); return; }
        MoveTargets.Clear();
        if (IsFolder) MoveTargets.Add(new MoveTarget($"⬆  Out to “{_parentTitle ?? "board"}”", null));
        foreach (var f in FolderTiles.OfType<BoardCardRef>())
            MoveTargets.Add(new MoveTarget(f.Name, f.CollectionId));
        SelectedMoveTarget = MoveTargets.FirstOrDefault();
        IsMoveSheetOpen = true;
    }

    [RelayCommand]
    private void CloseMoveSheet() => IsMoveSheetOpen = false;

    [RelayCommand]
    private async Task ConfirmMoveAsync()
    {
        if (_curation is null || SelectedAsset is not { } tile || _collectionId is not int fromId
            || SelectedMoveTarget is not { } target)
            return;
        var dest = target.CollectionId ?? _parentId;
        IsMoveSheetOpen = false;
        if (dest is not int toId) return;

        var title = tile.Model.Title ?? "image";
        try
        {
            await _curation.MoveAssetWithinBoardAsync(tile.Model.Id, fromId, toId);
            // The pin left this grid: drop the tile, refresh folder counts, close the detail panel.
            var node = _playing.Find(tile);
            if (node is not null) _playing.Remove(node);
            Assets.Remove(tile);
            SelectedAsset = null;
            await LoadFoldersAsync();
            _toasts.Show($"Moved “{title}”.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't move: {ex.Message}", isError: true); }
    }

    // ── Sync (re-fetch this board from its sources) ────────────────────────────

    [ObservableProperty] private bool _isSyncSheetOpen;
    [ObservableProperty] private string _syncCookiesBrowser = BrowserCookies.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncable))]
    [NotifyPropertyChangedFor(nameof(SyncButtonText))]
    private int _sourceCount;

    public IReadOnlyList<string> CookieBrowsers { get; } = BrowserCookies.Choices;

    /// <summary>A real board (not "All images") with at least one URL'd source can be synced.</summary>
    public bool IsSyncable => _collectionId is not null && SourceCount > 0;
    public string SyncButtonText => SourceCount == 1 ? "Sync 1 source" : $"Sync {SourceCount} sources";

    private async Task LoadSourcesAsync()
    {
        if (_library is null || _collectionId is not int id) return;
        _sourceUrls = await _library.GetBoardSourceUrlsAsync(id);
        SourceCount = _sourceUrls.Count;
    }

    [RelayCommand]
    private void OpenSyncSheet()
    {
        SyncCookiesBrowser = BrowserCookies.None;
        IsSyncSheetOpen = true;
    }

    [RelayCommand]
    private void CloseSyncSheet() => IsSyncSheetOpen = false;

    /// <summary>
    /// Re-fetch this board from each of its sources: the standard import pipeline run per source, so it pulls in
    /// anything missing/new (an interrupted import, lost items, or items the source gained later) while skipping
    /// already-held items and <b>tombstoned (blacklisted) ones</b> — those are never resurrected. Progress flows
    /// through the shared <see cref="ImportStatus"/>, so the inline strip shows it and new pins stream in live;
    /// the grid reloads when it finishes.
    /// </summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (_ingest is null || _collectionId is not int boardId || _sourceUrls.Count == 0) return;

        // The whole pipeline shares one ImportStatus; starting a sync while a Library import (or another sync) is
        // mid-flight would clobber its live count + streamed pins. Refuse rather than overlap.
        if (_importStatus.IsImporting)
        {
            _toasts.Show("An import is already running — wait for it to finish before syncing.");
            return;
        }

        var cookies = BrowserCookies.Resolve(SyncCookiesBrowser);
        if (!cookies.Found) { _toasts.Show(cookies.Error!, isError: true); return; }

        IsSyncSheetOpen = false;
        _importStatus.Begin(boardId); // drives IsBoardImporting + streams LastImported into this open board

        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            DownloadArchivePath = _projects?.Current?.DownloadArchivePath,
        };
        var processed = 0;
        var progress = new Progress<IngestProgress>(p =>
        {
            if (p.Phase is IngestPhase.Downloading or IngestPhase.Storing)
                _importStatus.Text = $"Syncing… {processed + p.Processed} so far";
            if (p.ImportedAsset is { } asset)
            {
                _importStatus.LastImportedCollectionId = p.ImportedIntoCollectionId; // set first — read on the pin change
                _importStatus.LastImported = asset;
            }
        });

        var newCount = 0;
        try
        {
            foreach (var url in _sourceUrls)
            {
                var result = await _ingest.ImportAsync(url, options, progress, boardId);
                newCount += result.NewAssets;
                processed += result.TotalItems;
            }
            _toasts.Show(newCount == 0
                ? "Sync complete — already up to date."
                : $"Synced — {newCount} new image(s).");
        }
        catch (Exception ex)
        {
            _toasts.Show($"Sync failed: {ex.Message}", isError: true);
        }
        finally
        {
            _importStatus.End(); // → OnImportStatusChanged reloads the grid
        }
    }

    [RelayCommand]
    private void Back() => _requestBack();

    [RelayCommand]
    private void CloseDetails() => SelectedAsset = null;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    // Load full metadata for the detail panel whenever the selected tile changes.
    partial void OnSelectedAssetChanged(AssetTileViewModel? value)
    {
        OnPropertyChanged(nameof(CanMoveSelected)); // the Move action depends on there being a live selection
        _ = LoadDetailsAsync(value);
    }

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

    private AssetTileViewModel NewTile(AssetView v) => new(v, _thumbnails, ActivateTile, UnloadGif, RefetchTile);

    /// <summary>
    /// Re-download a tile whose blob has gone missing from the store (deleted/moved/corrupted outside the app)
    /// from its saved source URL, then show it live again in place. Triggered by the tile's "file missing"
    /// re-download button.
    /// </summary>
    public async void RefetchTile(AssetTileViewModel tile)
    {
        if (_ingest is null || tile.IsRefetching) return;
        tile.IsRefetching = true;
        var token = _disposeCts.Token; // captured before the await — safe to read even after the CTS is disposed
        try
        {
            var view = await _ingest.RefetchAsync(tile.Model.Id, token);
            // Completed-just-before-Cancel race: if we navigated away during the await, drop it silently rather
            // than mutating the dead tile / toasting over the screen we left (the catch only covers a thrown cancel).
            if (token.IsCancellationRequested) return;
            if (view is null)
            {
                _toasts.Show("Couldn't re-download — the item may no longer exist.", isError: true);
                return;
            }
            tile.ApplyUpdate(view);
            _ = tile.EnsureThumbnailAsync();
            _toasts.Show($"Re-downloaded “{view.Title ?? "image"}”.");
        }
        catch (OperationCanceledException)
        {
            // Navigated away from the board mid-refetch (the VM was disposed) — drop it silently.
        }
        catch (Exception ex)
        {
            _toasts.Show($"Re-download failed: {ex.Message}", isError: true);
        }
        finally
        {
            tile.IsRefetching = false;
        }
    }
}
