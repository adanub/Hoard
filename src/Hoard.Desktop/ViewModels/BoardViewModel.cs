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

/// <summary>Where a board screen points: a specific board, or the whole project ("All images").
/// <see cref="ParentId"/> is set when drilling into a child folder (a Pinterest section / sub-folder),
/// carrying the parent so the folder screen can offer "move up" + edit-this-folder. (Searching happens live
/// through the floating bar — a target is never pre-filtered.)</summary>
public sealed record BoardTarget(
    int? CollectionId, string Title, int? ParentId = null, string? ParentTitle = null);

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
public partial class BoardViewModel : ViewModelBase, IDisposable, IResumable, IAbsorbsBack,
    ICrumbTitled, IProvidesSearch, IProvidesPlusActions, IImmersivePage
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
    private readonly NavigationService _nav;
    private readonly Action<BoardTarget> _openBoard;
    // Cross-page forward can apply a band/zoom history step before this (rebuilt) board's async asset load finishes;
    // these hold the requested state until LoadAssetsAsync resolves it.
    private bool _assetsLoaded;
    private int? _pendingBandAssetId;
    private bool _pendingZoom;
    // Set first thing in Dispose. Async completions (grid/folder loads) landing after the board was navigated away
    // from MUST check this and bail: an unguarded resume used to refill the disposed VM's Assets (re-rooting the
    // graph Dispose just emptied) and mutate the LIVE nav history from a dead page.
    private bool _disposed;
    // Monotonic load ids: only the LATEST in-flight grid/folder load may apply its results (the Lightbox _loadId
    // idiom). Kills the overlap race where a ctor load and an import-end load both snapshot firstLoad=true and the
    // straggler stomps the winner's applied band state.
    private int _loadSeq;
    private int _folderLoadSeq;
    private CancellationTokenSource? _searchDebounce;
    // Cancelled when the board is navigated away from (disposed), so an in-flight re-download doesn't keep running
    // and mutate this dead VM / toast for a screen you've left.
    private readonly CancellationTokenSource _disposeCts = new();
    private IReadOnlyList<string> _sourceUrls = Array.Empty<string>();

    // GIFs the user has tapped (or, with the autoplay setting, scrolled into view) keep playing in the grid;
    // bounded (LRU, size from Settings) so memory stays sane.
    private readonly LinkedList<AssetTileViewModel> _playing = new();
    private readonly UiSettingsStore? _uiSettingsStore;
    private readonly UiSettings? _uiSettings;

    public BoardViewModel(
        LibraryService library, CurationService curation, IngestService ingest,
        ThumbnailCache? thumbnails, ToastService toasts, ImportStatus importStatus, ProjectManager? projects,
        BoardTarget target, NavigationService nav, Action<BoardTarget> openBoard, UiSettingsStore? uiSettings = null)
    {
        _uiSettingsStore = uiSettings;
        _uiSettings = uiSettings?.Settings;
        // Settings saves notify here so GIF changes apply to THIS open board immediately (a lowered budget
        // trims the playing LRU; the view rescans) instead of waiting for the next tap or scroll.
        if (_uiSettingsStore is not null) _uiSettingsStore.Changed += OnUiSettingsChanged;
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
        _nav = nav;
        _openBoard = openBoard;
        FolderEditor = new BoardCardEditor(
            library, curation, thumbnails, toasts, "folder",
            removeCard: r => { FolderTiles.Remove(r); r.Dispose(); },
            afterChange: () => OnPropertyChanged(nameof(CanMoveSelected)));

        // The floating bar's ＋ menu: Sync appears once the source load proves there's something to sync
        // (OnSourceCountChanged), and disables while an import runs. The virtual "All images" / results
        // board contributes nothing (no sources, no folder row), which hides the ＋ button entirely.
        _syncAction = new PlusAction("Sync board", OpenSyncSheetCommand) { IsVisible = IsSyncable };
        PlusActions = _collectionId is null
            ? Array.Empty<PlusAction>()
            : new[] { _syncAction, new PlusAction("New folder", OpenNewFolderSheetCommand) };

        _importStatus.PropertyChanged += OnImportStatusChanged;
        Assets.CollectionChanged += (_, _) => SyncExpandedIndex(); // keep the band on the selected tile as items shift
        Infrastructure.LeakCanary.Track(this);
        UpdateImportState();
        _ = LoadAssetsAsync();
        _ = LoadSourcesAsync(); // for the Sync action (a real board with at least one URL'd source)
        _ = LoadFoldersAsync(); // child folders (Pinterest sections / sub-folders) shown above the grid
    }

    // Design-time constructor for the XAML previewer. A throwaway NavigationService keeps _nav non-null so the band/
    // zoom code paths don't need a null-fallback (its steps just apply against a null Current = no-op at design time).
    public BoardViewModel() : this(null!, null!, null!, null, new ToastService(), new ImportStatus(), null, new BoardTarget(null, "All images"), new NavigationService(), _ => { }) { }

    [ObservableProperty] private string _title;
    public ObservableCollection<AssetTileViewModel> Assets { get; } = new();

    // ── Shell chrome (breadcrumb + floating bar) ─────────────────────────────

    /// <summary>The crumb carries the live result count while a search is active — "Terrain Ideas
    /// (2 folders · 12 items found)". Re-raised when the query, the (debounced) filtered grid, or the
    /// folder-name filter changes, so the shell rebuilds the trail.</summary>
    public string CrumbTitle => IsSearchActive ? $"{Title} ({SearchSummary})" : Title;

    /// <summary>True while the floating bar's search query is non-empty (drives the crumb's result count,
    /// the folder-name filter, and hides the "+ New folder" tile — a search grid isn't a place to create).</summary>
    public bool IsSearchActive => SearchQuery.Trim().Length > 0;

    private string SearchSummary
    {
        get
        {
            var items = Assets.Count;
            var folders = FolderTiles.OfType<BoardCardRef>().Count(f => !f.IsFilteredOut);
            var itemsText = items == 1 ? "1 item found" : $"{items} items found";
            return folders > 0
                ? $"{(folders == 1 ? "1 folder" : $"{folders} folders")} · {itemsText}"
                : itemsText;
        }
    }

    // The images reload through the debounced query, but folder cards filter by NAME instantly, in memory —
    // same hidden-not-removed pattern as the Library/launcher card filters (no cover churn while typing).
    private void ApplyFolderSearchFilter()
    {
        var q = SearchQuery.Trim();
        foreach (var f in FolderTiles.OfType<BoardCardRef>())
            f.IsFilteredOut = q.Length > 0 && !f.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CrumbTitle));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CrumbTitle));

    private readonly PlusAction _syncAction;

    /// <summary>The floating bar's ＋ menu for this screen.</summary>
    public IReadOnlyList<PlusAction> PlusActions { get; }

    /// <summary>The floating bar's search: the existing live (debounced) grid filter.</summary>
    public string SearchText
    {
        get => SearchQuery;
        set => SearchQuery = value;
    }

    public string SearchPlaceholder => _collectionId is null ? "Search all images…" : "Search this board…";

    public void SubmitSearch() { } // live filter — Enter has nothing extra to do

    partial void OnSourceCountChanged(int value) => _syncAction.IsVisible = IsSyncable;
    partial void OnIsBoardImportingChanged(bool value) => _syncAction.IsEnabled = !value;

    /// <summary>The fullscreen zoom is immersive — the floating bar hides while it's open.</summary>
    public bool IsImmersive => IsLightboxOpen;

    partial void OnIsLightboxOpenChanged(bool value) => OnPropertyChanged(nameof(IsImmersive));

    [ObservableProperty] private AssetTileViewModel? _selectedAsset;
    /// <summary>Index of <see cref="SelectedAsset"/> in <see cref="Assets"/> (-1 when nothing is expanded), bound
    /// to the masonry layout so it lays that item out as the full-width inline detail band.</summary>
    [ObservableProperty] private int _expandedIndex = -1;
    [ObservableProperty] private AssetDetailViewModel? _details;
    /// <summary>True while the selected asset's detail is loading, so the band shows a spinner instead of a blank
    /// rail (the band opens on <c>IsExpanded</c>, which flips before the async detail load completes).</summary>
    [ObservableProperty] private bool _isDetailLoading;

    // The detail VM owns a native preview bitmap — free the outgoing one on EVERY swap (switch image, close band,
    // board dispose), same eager-release rule as tile thumbnails; dropping the reference would leave the surface
    // to lagging finalization, one per band open.
    partial void OnDetailsChanged(AssetDetailViewModel? oldValue, AssetDetailViewModel? newValue) => oldValue?.Dispose();

    /// <summary>True when the grid is narrower than the band's stack breakpoint, so the inline detail band stacks
    /// the image over the info area instead of placing the info rail beside it. The view sets this from the grid
    /// width (using the packer's own breakpoint, which also drives the band's stacked <i>height</i>), so the
    /// layout and the height stay in agreement.</summary>
    [ObservableProperty] private bool _isBandStacked;
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
        _disposed = true; // in-flight grid/folder loads check this on resume and apply nothing to the dead VM
        Infrastructure.LeakCanary.MarkDead(this);
        if (_uiSettingsStore is not null) _uiSettingsStore.Changed -= OnUiSettingsChanged;
        _importStatus.PropertyChanged -= OnImportStatusChanged;
        CollapseStarting = null; // drop the view's handler so a disposed board doesn't keep its (detached) view alive
        // Cancel (not just Dispose) so a debounced search/folder-reload mid-Task.Delay bails instead of waking up
        // to query + rebuild on this disposed VM — Dispose() alone does NOT cancel a pending Task.Delay.
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _folderReloadDebounce?.Cancel();
        _folderReloadDebounce?.Dispose();
        _disposeCts.Cancel(); // abort any in-flight re-download before it touches this disposed VM
        _disposeCts.Dispose();
        // Free every tile's thumbnail (native Skia memory) eagerly and drop the tile→board callbacks, rather than
        // waiting on GC finalization once the board is collected — so leaving a board unloads its images, and a tile
        // that Avalonia's ItemsRepeater still retains (its cached element event-args pin the last container) can't
        // keep the board alive. (Played GIFs' frames are already released when their AnimatedImageControl is torn
        // down with the view.)
        foreach (var tile in Assets) tile.Dispose();
        _playing.Clear();
        // Ask the (still-attached) view to force its ItemsRepeater through one synchronous layout pass so it recycles
        // and DETACHES its realized tiles now — see ViewTeardown. This is the load-bearing step: a tile's #Root
        // self-binding, kept active while the tile stays attached and held by a compositor-retained child, otherwise
        // roots the entire BoardView (GC-root analysis: compositor → Button → NamedElementNode #Root → ItemCard →
        // BoardView). Detaching the tiles deactivates those bindings. Done BEFORE Assets.Clear so the repeater still
        // has its source bound when the view nulls it.
        ViewTeardown?.Invoke();
        Assets.Clear();
        // Clearing the selection also frees the detail preview synchronously: OnSelectedAssetChanged →
        // LoadDetailsAsync(null) sets Details = null before its first await, and OnDetailsChanged disposes the
        // outgoing detail VM (its native preview bitmap with it).
        SelectedAsset = null;
        FolderTiles.DisposeAndClear(); // frees the folder cards' native cover bitmaps
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
        var seq = ++_folderLoadSeq;

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
        // Disposed (navigated away) or superseded (the debounced import reload fired again) while querying: apply
        // nothing — a stale resume used to repopulate the dead VM's folder row and decode covers nothing would free.
        if (_disposed || seq != _folderLoadSeq) return;

        FolderTiles.DisposeAndClear();
        foreach (var t in tiles) FolderTiles.Add(t);
        ApplyFolderSearchFilter(); // rebuilt cards start unfiltered — reapply the bar's live query
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
            _nav.DropCurrentStates(); // the moved image's band step is now stale
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
        // Pre-select the user's default cookies browser (Settings), falling back to "(none)".
        SyncCookiesBrowser = BrowserCookies.NormaliseChoice(_uiSettings?.DefaultCookiesBrowser);
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
        // And a background remote (Backup) sync copies the very files this sync would write — refuse that too.
        if (_importStatus.IsRemoteSyncing)
        {
            _toasts.Show("A backup sync is running — wait for it to finish before syncing.");
            return;
        }

        var cookies = BrowserCookies.Resolve(SyncCookiesBrowser);
        if (!cookies.Found) { _toasts.Show(cookies.Error!, isError: true); return; }

        IsSyncSheetOpen = false;
        _importStatus.Begin(boardId); // drives IsBoardImporting + streams LastImported into this open board

        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            DownloadArchivePath = _projects?.Current is { } project ? _projects.DownloadArchivePathFor(project) : null,
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
        // Observe the dispose token (like ImportAsync/RefetchTile do) so backing out of the board mid-sync stops the
        // crawl instead of the still-running state machine keeping this whole disposed board graph (Assets + every
        // tile VM) rooted for the duration — the sync belongs to the screen, not the app.
        var ct = _disposeCts.Token;
        try
        {
            foreach (var url in _sourceUrls)
            {
                ct.ThrowIfCancellationRequested();
                var result = await _ingest.ImportAsync(url, options, progress, boardId, ct);
                newCount += result.NewAssets;
                processed += result.TotalItems;
            }
            _toasts.Show(newCount == 0
                ? "Sync complete — already up to date."
                : $"Synced — {newCount} new image(s).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Board was navigated away from mid-sync. Say so — the card's progress strip just vanishing reads as
            // "sync complete", and a partial sync is invisible until the next one. (The shell owns the toast host,
            // so this shows over whatever screen the user is on now.) A re-sync picks up exactly where this stopped.
            _toasts.Show($"Sync stopped — {processed} item(s) checked before you left the board. Sync again to finish.");
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

    // ── Fullscreen zoom/pan lightbox for the selected image ───────────────────────

    [ObservableProperty] private bool _isLightboxOpen;
    [ObservableProperty] private string? _lightboxSource;
    [ObservableProperty] private bool _lightboxIsGif;

    /// <summary>Open the fullscreen zoom on the selected live image/GIF as a <b>history step</b> (Back/Esc closes it
    /// back to the band; forward re-opens it). A video, a tombstone, or a live-but-missing blob has nothing to view,
    /// so it's a no-op — and so is a click while the band is animating CLOSED (the fading band stays hit-testable and
    /// the selection isn't cleared until the collapse finishes, so without the gate a mid-fade click would push a
    /// zoom step that the collapse immediately force-shuts, stranding a ghost step). Triggered by tapping the
    /// expanded band's media.</summary>
    public void OpenLightbox()
    {
        if (_closing) return;
        if (SelectedAsset is not { IsDeleted: false, IsFileMissing: false } tile || !tile.IsImage) return;
        _nav.PushState(NavShowZoom, NavHideZoom);
    }

    // Closing the zoom is a back step (so it leaves the right forward entry); the ✕ / scrim raise this.
    [RelayCommand]
    private void CloseLightbox() => _nav.Back();

    // Apply/Revert for the zoom history step, taken against Current so a cross-page forward re-opens it on the
    // freshly-rebuilt board. Apply reports whether the zoom actually opened (or was validly deferred).
    private static bool NavShowZoom(ViewModelBase? cur) => (cur as BoardViewModel)?.ShowZoom() ?? false;
    private static void NavHideZoom(ViewModelBase? cur) => (cur as BoardViewModel)?.HideZoom();

    private bool ShowZoom()
    {
        if (SelectedAsset is not { IsDeleted: false, IsFileMissing: false } tile || !tile.IsImage)
        {
            // Only stash a pending zoom before the (rebuilt) board has loaded — a cross-page forward applies the zoom
            // step before its band resolves (a valid deferral). After the load, an invalid selection (video /
            // tombstone / missing) just can't be zoomed — report failure so no ghost step is recorded.
            if (!_assetsLoaded) { _pendingZoom = true; return true; }
            return false;
        }
        LightboxSource = tile.Model.AbsolutePath;
        LightboxIsGif = tile.IsGif;
        IsLightboxOpen = true;
        return true;
    }

    private void HideZoom()
    {
        _pendingZoom = false; // the step is leaving history — a latched zoom must not fire after the load (see HideBand)
        IsLightboxOpen = false;
    }

    // ── Delete the selected pin (in-app note sheet → restorable tombstone) ─────────
    // Replaces the old DeleteDialog OS window: a SheetHost collecting the required reason, matching the other
    // in-app sheets (Move / New folder / Sync). The note is mandatory — Confirm stays disabled until one's typed.

    [ObservableProperty] private bool _isDeleteSheetOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmDelete))]
    private string _deleteNote = "";

    [ObservableProperty] private string _deleteTargetTitle = "";

    // The sheet's target, CAPTURED at open — Confirm must not re-read SelectedAsset, because a grid reload (a sync
    // finishing) can clear the selection while the sheet sits open: the delete would then silently no-op while the
    // user believes the image was tombstoned. The captured tile stays valid (delete goes by its asset id).
    private AssetTileViewModel? _deleteTarget;

    /// <summary>The delete sheet's Confirm is enabled only once a (non-blank) reason has been entered.</summary>
    public bool CanConfirmDelete => !string.IsNullOrWhiteSpace(DeleteNote);

    /// <summary>Open the in-app delete sheet for the selected live pin (collects the required tombstone reason).</summary>
    public void OpenDeleteSheet()
    {
        if (SelectedAsset is not { IsDeleted: false } tile) return;
        _deleteTarget = tile;
        DeleteTargetTitle = tile.Model.Title is { Length: > 0 } t ? t : "this image";
        DeleteNote = "";
        IsDeleteSheetOpen = true;
    }

    [RelayCommand]
    private void CloseDeleteSheet() { IsDeleteSheetOpen = false; _deleteTarget = null; }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var note = DeleteNote.Trim();
        if (note.Length == 0) return; // guard: Confirm shouldn't be enabled, but never tombstone with a blank note
        var target = _deleteTarget;
        _deleteTarget = null;
        IsDeleteSheetOpen = false;
        if (target is not null) await DeleteAsync(target, note);
    }


    // The band's ✕: closing the band is a back step (pops the band history step → animated collapse via Revert).
    [RelayCommand]
    private void CloseDetails() => _nav.Back();

    /// <summary>While the detail band is animating closed, absorb a Back/Esc so a rapid second gesture doesn't pop the
    /// whole board out from under the collapse (the band's history step is already reverted, but <see cref="SelectedAsset"/>
    /// stays set until <see cref="FinishCollapse"/>, so the next Back would otherwise hit the page step).</summary>
    public bool AbsorbBack() => _closing;

    // ── Closing the inline band is a sequence (fade the band out, THEN reflow the tiles back), so it's split
    //    across the view: RequestCollapse → (view fades) → CommitCollapse → (layout reflows) → FinishCollapse.

    /// <summary>Raised when a user-initiated close begins: the view fades the band out, then calls
    /// <see cref="CommitCollapse"/>. With no view attached the collapse is immediate.</summary>
    public event Action? CollapseStarting;

    /// <summary>Raised at the start of <see cref="Dispose"/>, while the view is (still) attached — the shell swaps the
    /// page only on the NEXT layout pass — so the view can force its <c>ItemsRepeater</c> to recycle + detach its
    /// realized tiles NOW. A <i>detached</i> repeater never runs layout again, so it would otherwise keep every
    /// realized tile alive; and a tile's <c>#Root</c> self-binding (held by a compositor-retained child) pins the
    /// whole board through it. Detaching the tiles deactivates those bindings and breaks the chain.</summary>
    public event Action? ViewTeardown;

    // True between CommitCollapse and FinishCollapse: the selection/expanded tile is kept alive (invisible) while
    // the tiles reflow back, so SyncExpandedIndex must report -1 (the band is already leaving the layout).
    private bool _collapsing;
    // True for the whole close — from RequestCollapse until the selection is cleared — covering the fade BEFORE
    // _collapsing is set. AbsorbBack() reads it so a second Back/Esc during the animation is absorbed.
    private bool _closing;

    /// <summary>Begin closing the band: ask the view to fade it out first (it then calls <see cref="CommitCollapse"/>).
    /// Falls back to an immediate collapse when nothing is listening (design-time / no view).</summary>
    public void RequestCollapse()
    {
        if (SelectedAsset is null) return;
        _closing = true;
        if (CollapseStarting is null) { CommitCollapse(); FinishCollapse(); return; }
        CollapseStarting.Invoke();
    }

    /// <summary>Step 2 of a close (after the fade-out): drop the band from the layout so the tiles reflow back up,
    /// keeping the expanded tile selected-but-hidden (IsExpanded stays true) so it doesn't flash as a stretched
    /// tile mid-reflow.</summary>
    public void CommitCollapse()
    {
        if (SelectedAsset is null) return;
        _collapsing = true;
        ExpandedIndex = -1; // → the layout tweens the tiles back to the band-free packing
    }

    /// <summary>Step 3 of a close (after the reflow-back settles): clear the selection — the tile chrome returns
    /// and the band element is released.</summary>
    public void FinishCollapse()
    {
        if (!_collapsing) return;
        _collapsing = false;
        SelectedAsset = null;
    }

    /// <summary>The board's view is detaching while the band is still open/closing because we navigated AWAY from it
    /// (drilling into a child folder: <see cref="NavigationService.Push"/> reverts the band step, but its animated
    /// collapse can't finish on a view that's leaving). Drop the band state synchronously so this board — kept on the
    /// stack beneath the new page — isn't revealed half-open when the user comes back to it.</summary>
    public void AbandonBand()
    {
        // A latched-but-unresolved band/zoom (forward into a still-loading board, then navigated away) dies with
        // the abandonment — its history steps were reverted by the Push.
        _pendingBandAssetId = null;
        _pendingZoom = false;
        // The flags can't be set while the selection is null (RequestCollapse/CommitCollapse guard on it), and
        // OnSelectedAssetChanged clears them on any real change — so clearing the selection is the whole job.
        SelectedAsset = null;
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    // The selected tile is the one expanded inline into the detail band: flip its IsExpanded, keep the masonry's
    // ExpandedIndex in sync, and load its metadata for the band rail.
    partial void OnSelectedAssetChanged(AssetTileViewModel? oldValue, AssetTileViewModel? newValue)
    {
        // The deselected tile collapses; reset its band opacity (while it's hidden, so no flash) so re-expanding
        // it fades fresh from 0.
        if (oldValue is not null) { oldValue.IsExpanded = false; oldValue.BandContentOpacity = 0; }
        if (newValue is not null) newValue.IsExpanded = true;
        // The lightbox is tied to the selection; if the selection is cleared (a move, or a grid reload after
        // import/sync), close it so it can't linger over a stale image whose tile is gone.
        else IsLightboxOpen = false;
        // Any genuine selection change ends a close-in-progress — covers a reload/move that clears SelectedAsset
        // mid-collapse, which would otherwise leave _collapsing/_closing stuck true and SyncExpandedIndex pinned at -1.
        _collapsing = false;
        _closing = false;
        SyncExpandedIndex();
        OnPropertyChanged(nameof(CanMoveSelected)); // the Move action depends on there being a live selection
        _ = LoadDetailsAsync(newValue);
    }

    // The expanded item's index shifts when tiles stream in/out (e.g. a live import inserts at the top), so
    // recompute it whenever the collection changes — the band must follow the selected tile, not a stale index.
    // While collapsing, report -1 (the band is leaving the layout) even though the tile is still selected.
    private void SyncExpandedIndex() =>
        ExpandedIndex = _collapsing || SelectedAsset is null ? -1 : Assets.IndexOf(SelectedAsset);

    // Reload after a short pause so we don't query on every keystroke. SearchText (the floating bar's alias
    // for this query) notifies alongside so the bar stays in sync when the query is set programmatically.
    // Folder cards filter instantly (in-memory, no debounce needed); the crumb's count follows via the
    // filter's CrumbTitle raise and again when the debounced grid reload applies.
    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearchActive));
        ApplyFolderSearchFilter();
        _ = DebouncedSearchAsync();
    }

    private async Task LoadDetailsAsync(AssetTileViewModel? tile)
    {
        Details = null; // clear immediately so a switch never shows the PREVIOUS asset's metadata while this loads
        if (_library is null || tile is null) { IsDetailLoading = false; return; }
        IsDetailLoading = true;
        try
        {
            var detail = await _library.GetAssetDetailAsync(tile.Model.Id);
            if (ReferenceEquals(SelectedAsset, tile)) // ignore if selection changed while loading
                Details = detail is null ? null : new AssetDetailViewModel(detail);
        }
        finally
        {
            if (ReferenceEquals(SelectedAsset, tile)) IsDetailLoading = false;
        }
    }

    private Task ReloadDetailsAsync() => LoadDetailsAsync(SelectedAsset);

    /// <summary>Tapping a tile expands it inline into the detail band. The band is a navigation step: opening from
    /// the grid pushes it, switching to another image while open replaces it, and tapping the open tile again goes
    /// back (closes it) — so it sits in the same back/forward stack as pages. Independently, a tapped GIF starts
    /// autoplaying in its tile and keeps playing (bounded LRU) whether or not it's expanded.</summary>
    public void ActivateTile(AssetTileViewModel tile)
    {
        if (ReferenceEquals(SelectedAsset, tile)) _nav.Back();                         // tap the open tile again → close the band
        else BandState(tile.Model.Id, replace: SelectedAsset is not null);            // grid → band, or switch (replace, no stacking)
        if (tile.IsGif) PlayGif(tile);
    }

    /// <summary>True when the autoplay setting is on, so the view can skip its viewport scans entirely.</summary>
    public bool GifAutoplayEnabled => _uiSettings is { GifAutoplay: true };

    // A Settings save changed something: apply a lowered GIF budget to what's already playing right away, and
    // raise unconditionally so the view rescans — that's what makes a RAISED budget start more visible GIFs,
    // and it's a debounced no-op for saves that didn't touch GIFs (the rescan gates on the setting itself).
    private void OnUiSettingsChanged()
    {
        if (_disposed) return;
        TrimPlayingToBudget();
        OnPropertyChanged(nameof(GifAutoplayEnabled));
    }

    /// <summary>Autoplay (Settings): play the GIFs whose tiles are actually IN the viewport — called by the
    /// view, debounced, after scrolling/reflow settles, with <paramref name="visible"/> in TOP-TO-BOTTOM
    /// viewport order. Deliberately NOT realization-driven: the repeater realizes a buffer beyond the
    /// viewport, last, so playing-on-realize let off-screen GIFs evict the on-screen ones from the LRU
    /// (memory climbed while nothing visible animated). Capped at the budget — playing every visible GIF
    /// through the LRU made each scan cycle the excess through play→evict (decode churn + on-screen flicker
    /// whenever visible GIFs outnumbered the budget); the topmost <c>max</c> play, stably, and eviction
    /// falls on scrolled-past ones.</summary>
    public void AutoplayVisibleGifs(IReadOnlyList<AssetTileViewModel> visible)
    {
        if (!GifAutoplayEnabled) return;
        var budget = MaxPlaying;
        var played = 0;
        foreach (var tile in visible)
        {
            if (played >= budget) break;
            if (!tile.IsGif || tile.IsDeleted || tile.IsFileMissing) continue;
            PlayGif(tile);
            played++;
        }
    }

    private int MaxPlaying => Math.Max(1, _uiSettings?.MaxPlayingGifs ?? 12); // 12 = the pre-Settings bound

    // Start (or refresh the LRU position of) one playing GIF; the least-recently-played beyond the budget stop.
    private void PlayGif(AssetTileViewModel tile)
    {
        var existing = _playing.Find(tile);
        if (existing is not null) { _playing.Remove(existing); _playing.AddFirst(existing); return; }

        tile.IsPlaying = true;
        _playing.AddFirst(tile);
        TrimPlayingToBudget();
    }

    private void TrimPlayingToBudget()
    {
        var max = MaxPlaying;
        while (_playing.Count > max)
        {
            var oldest = _playing.Last!.Value;
            _playing.RemoveLast();
            oldest.IsPlaying = false; // stop the least-recently-played GIF
        }
    }

    // ── Band history step (open/switch/close routed through NavigationService) ──────

    // Open the band as a history step, or — when one's already open — REPLACE the top step so switching images
    // doesn't stack band-A under band-B (Back from the band leaves straight to the grid).
    private void BandState(int assetId, bool replace)
    {
        bool Apply(ViewModelBase? cur) => NavShowBand(cur, assetId);
        if (replace) _nav.ReplaceTopState(Apply, NavHideBand);
        else _nav.PushState(Apply, NavHideBand);
    }

    // Apply/Revert for the band step, taken against Current so a cross-page forward re-expands the right image on
    // the freshly-rebuilt board. Apply reports whether the band actually opened (or was validly deferred) so a
    // forward onto a since-deleted asset drops the step instead of recording a ghost.
    private static bool NavShowBand(ViewModelBase? cur, int assetId) => (cur as BoardViewModel)?.ShowBandFor(assetId) ?? false;
    private static void NavHideBand(ViewModelBase? cur) => (cur as BoardViewModel)?.HideBand();

    private bool ShowBandFor(int assetId)
    {
        var tile = Assets.FirstOrDefault(t => t.Model.Id == assetId);
        if (tile is null)
        {
            // Not loaded yet → a cross-page forward into a fresh board; remember it and apply after LoadAssetsAsync
            // (a valid deferral). On a LOADED grid the asset is simply gone — report failure so no step is recorded.
            if (!_assetsLoaded) { _pendingBandAssetId = assetId; return true; }
            return false;
        }
        SelectedAsset = tile;
        return true;
    }

    private void HideBand()
    {
        // The step is leaving history — a latched-but-unresolved band (backed out of before the rebuilt board
        // finished loading) must not fire later, or the grid would open a band whose step is gone.
        _pendingBandAssetId = null;
        RequestCollapse();
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

    /// <summary>Tombstone an asset with a note: its blob is freed and a Remove op logged, but the tile stays in
    /// place (now showing the note) and can be restored. Updated in place so scroll survives. Takes the sheet's
    /// CAPTURED target (not the live selection — a reload may have cleared/replaced it while the sheet was open).</summary>
    public async Task DeleteAsync(AssetTileViewModel tile, string note)
    {
        if (_curation is null || tile.IsDeleted) return;

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
        if (_disposed) return; // navigated away while deleting — the DB change stands; don't touch the dead VM

        // A reload while the sheet was open replaced the tiles: update the LIVE tile for this asset (the captured
        // one may no longer be in the grid), so the tombstone shows immediately either way.
        var live = Assets.FirstOrDefault(t => t.Model.Id == tile.Model.Id) ?? tile;
        var node = _playing.Find(live);
        if (node is not null) _playing.Remove(node);
        _thumbnails?.Evict(sha);

        var title = live.Model.Title ?? "image";
        live.ApplyUpdate(live.Model with { IsDeleted = true, DeletionNote = note });
        await ReloadDetailsAsync();
        _toasts.Show($"Deleted “{title}” — kept as a restorable tombstone.");
    }

    /// <summary>Restore the selected tombstone by re-downloading its original media, then show it live again.
    /// Observes the dispose token (like <see cref="RefetchTile"/>): a restore completing after the board was
    /// navigated away from must not re-arm the disposed tile's thumbnail decode — that would pin a fresh native
    /// bitmap nothing will ever free — nor toast over whatever screen the user is on now.</summary>
    public async Task RestoreSelectedAsync()
    {
        if (_ingest is null || SelectedAsset is not { IsDeleted: true } tile) return;

        IsBusy = true;
        var token = _disposeCts.Token; // captured before the await — safe to read even after the CTS is disposed
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
        if (token.IsCancellationRequested) return; // navigated away mid-restore — the DB change stands; drop the UI work

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
        var seq = ++_loadSeq;
        var firstLoad = !_assetsLoaded;
        var views = await _library.GetAssetsAsync(_collectionId, SearchQuery);
        // Disposed while querying (navigated away), or superseded by a newer load (import-end overlapping the ctor
        // load, rapid search keystrokes): apply NOTHING — a stale resume used to refill the dead VM's tiles and
        // corrupt the nav history from off-screen.
        if (_disposed || seq != _loadSeq) return;
        // Tiles are being recreated: close the detail panel and forget what was playing so their GIF cache
        // leases drop and the frames free (memory tracks what's actually on screen).
        SelectedAsset = null;
        // A *reload* (import/sync/search) recreated the tiles, so any band/zoom history step is now stale — drop it
        // so back/forward stay coherent. The FIRST load is a (re)build: keep a forward-applied step and resolve it.
        // ONLY when this board is the page on screen: a board buried beneath a drilled-into folder also reloads when
        // its import ends, and it must not pop the CURRENT page's open band/zoom steps off the shared history.
        if (!firstLoad && ReferenceEquals(_nav.Current, this)) _nav.DropCurrentStates();
        _playing.Clear();
        Assets.Clear();
        foreach (var v in views)
            Assets.Add(NewTile(v));
        _assetsLoaded = true; // AFTER Assets is populated — ShowBandFor/ShowZoom read this to know the grid is ready
        OnPropertyChanged(nameof(CrumbTitle)); // the crumb's "(x items found)" tracks the applied result set
        if (firstLoad)
        {
            // Cross-page forward applied a band/zoom step before this rebuilt board finished loading — apply it now.
            // If the pending band's asset no longer exists (deleted while backed out), the applied steps are ghosts —
            // drop them (no revert; nothing ever opened) so they don't eat the user's next Back as a dead press.
            // The zoom only ever rides on the band, so it stands or falls with it.
            if (_pendingBandAssetId is int pid)
            {
                _pendingBandAssetId = null;
                if (!ShowBandFor(pid)) { _pendingZoom = false; _nav.DropCurrentStates(); }
            }
            if (_pendingZoom) { _pendingZoom = false; if (!ShowZoom()) _nav.DropCurrentStates(); }
        }
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
