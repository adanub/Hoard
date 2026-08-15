using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Hoard.Core.Sync;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;
using Hoard.Ingest.GalleryDl;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Desktop.ViewModels;

/// <summary>Marker for the leading "+ New board" tile in the Library grid (rendered by its own template).</summary>
public sealed class NewBoardTile : ViewModelBase
{
    public static readonly NewBoardTile Instance = new();
    private NewBoardTile() { }
}

/// <summary>
/// A board on the Library grid, shown as a <see cref="Controls.BoardCard"/>: name, item count, and a 3-up
/// collage cover from its newest images. <see cref="CollectionId"/> null = the virtual "All images" board.
/// Tapping the card opens it (the Board screen); the pencil edits it (wired once the board model lands).
/// </summary>
public partial class BoardCardRef : ViewModelBase, IDisposable
{
    public int? CollectionId { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private Bitmap? _thumb0;
    [ObservableProperty] private Bitmap? _thumb1;
    [ObservableProperty] private Bitmap? _thumb2;

    // The covers are native (Skia) bitmaps — free the outgoing surface on every swap and on Dispose (the eager-free
    // rule from AssetTileViewModel.Thumbnail); dropping the reference alone waits on lagging finalization.
    partial void OnThumb0Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();
    partial void OnThumb1Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();
    partial void OnThumb2Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>Set once the card leaves its grid. The (fire-and-forget) cover load checks it after every await so a
    /// decode finishing for a card a rebuild already disposed frees its bitmap instead of stranding it on the dead
    /// card — nothing would ever swap or dispose it again.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Free the collage covers' native bitmaps (grid rebuild, screen dispose).</summary>
    public void Dispose()
    {
        IsDisposed = true;
        Thumb0 = null;
        Thumb1 = null;
        Thumb2 = null;
    }

    // While this board is being imported into, the card shows a pinned progress strip + live count.
    [ObservableProperty] private bool _isImporting;

    /// <summary>Waiting its turn in a "sync all boards" run — see <see cref="Controls.BoardCard.IsQueued"/>.
    /// Never true at the same time as <see cref="IsImporting"/>: a board that starts stops being queued, and
    /// one that finishes clears both (only the boards still to come stay marked).</summary>
    [ObservableProperty] private bool _isQueued;

    [ObservableProperty] private string _importStatusText = "";

    /// <summary>True when the card doesn't match the floating bar's live board-name filter — hidden
    /// (collapsed) in the grid rather than removed, so its cover bitmaps aren't churned by typing.</summary>
    [ObservableProperty] private bool _isFilteredOut;

    // Edit-popup detail (loaded lazily when the pencil is clicked).
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _cacheText = "";
    [ObservableProperty] private string _addedText = "";
    [ObservableProperty] private string _importedText = "";
    public ObservableCollection<Controls.BoardSourceRef> Sources { get; } = new();

    public IRelayCommand OpenCommand { get; }
    /// <summary>Null when the board can't be edited (the "All images" card), which hides the pencil.</summary>
    public IRelayCommand? EditCommand { get; }

    public BoardCardRef(int? collectionId, string name, Action<BoardCardRef> open, Action<BoardCardRef>? edit)
    {
        CollectionId = collectionId;
        _name = name;
        OpenCommand = new RelayCommand(() => open(this));
        EditCommand = edit is null ? null : new RelayCommand(() => edit(this));
    }
}

/// <summary>An import target: a new board (CollectionId null) or an existing one to merge into.</summary>
public sealed record ImportTarget(string Display, int? CollectionId);

/// <summary>
/// The Library screen: a grid of the project's boards (as cards) led by "+ New board" and "All images", with
/// an Import action in the floating bar's ＋ menu. The bar's search live-filters the board cards by name
/// (image search lives inside the "All images" board); opening a card pushes the Board screen, and the shell's
/// back returns to Projects (switching project).
/// </summary>
public partial class LibraryViewModel : ViewModelBase, IResumable, IDisposable, ICrumbTitled, IProvidesSearch, IProvidesPlusActions
{
    private readonly IngestService _ingest;
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly ProjectManager _projects;
    private readonly ThumbnailCache? _thumbnails;
    private readonly ToastService _toasts;
    private readonly ImportStatus _importStatus;
    private readonly Action<BoardTarget> _openBoard;
    private readonly UiSettingsStore? _uiSettings;
    private readonly IDbContextFactory<HoardDbContext>? _dbFactory;
    private readonly ArchiveLog? _archive;
    private readonly BoardExporter? _exporter;
    // Cancelled when the Library is navigated away from (disposed), so an in-flight import doesn't keep running
    // and toast / rebuild the grid on this dead VM after the user has left.
    private readonly CancellationTokenSource _disposeCts = new();
    // Set first thing in Dispose: an in-flight RefreshAsync resuming after the Library was popped must apply
    // NOTHING — it used to repopulate the dead VM's Tiles and strand freshly-decoded native cover bitmaps
    // (the cards were created after Dispose, so nothing would ever free them). The BoardViewModel rule.
    private bool _disposed;

    public LibraryViewModel(
        IngestService ingest, LibraryService library, CurationService curation, ProjectManager projects,
        ThumbnailCache? thumbnails, ToastService toasts, ImportStatus importStatus,
        Action<BoardTarget> openBoard, UiSettingsStore? uiSettings = null,
        IDbContextFactory<HoardDbContext>? dbFactory = null, ArchiveLog? archive = null,
        BoardExporter? exporter = null)
    {
        _ingest = ingest;
        _library = library;
        _curation = curation;
        _projects = projects;
        _thumbnails = thumbnails;
        _toasts = toasts;
        _importStatus = importStatus;
        _openBoard = openBoard;
        _uiSettings = uiSettings;
        _dbFactory = dbFactory;
        _archive = archive;
        _exporter = exporter;
        BoardEditor = new BoardCardEditor(
            library, curation, thumbnails, toasts, "board",
            removeCard: r => { Tiles.Remove(r); r.Dispose(); }, loadExtraDetail: LoadBoardSourcesAsync);
        PlusActions = new[]
        {
            new PlusAction("Import board", OpenImportSheetCommand),
            new PlusAction("Sync all boards", OpenSyncAllSheetCommand),
            new PlusAction("Backup", OpenRemoteSheetCommand),
            new PlusAction("Export project", OpenExportSheetCommand),
        };
        // Watch the shared import state so a board card lights up whoever started the import — this grid's own
        // import sheet OR a board's Sync button (which drives only ImportStatus, not this grid directly).
        _importStatus.PropertyChanged += OnImportStatusChanged;
        _ = RefreshAsync();
    }

    /// <summary>True while THIS grid's <see cref="ImportAsync"/> owns the in-flight import — it updates the card +
    /// refreshes itself (and owns the failed-import discard), so the shared-status watcher mustn't double up.</summary>
    private bool _selfImporting;

    // Mirror the shared ImportStatus onto the matching board card so a running import/sync shows its progress
    // strip + live count here, even when it was started from the Board screen's Sync button.
    private void OnImportStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        var card = _importStatus.CollectionId is int id
            ? Tiles.OfType<BoardCardRef>().FirstOrDefault(r => r.CollectionId == id)
            : null;

        switch (e.PropertyName)
        {
            case nameof(ImportStatus.Text):
                if (card is not null) card.ImportStatusText = _importStatus.Text;
                break;
            case nameof(ImportStatus.IsImporting):
                if (card is not null) card.IsImporting = _importStatus.IsImporting;
                // Any in-flight import/sync (even one started from a Board screen) blocks a new Library import, so
                // re-evaluate the Import button against the shared status.
                ImportCommand.NotifyCanExecuteChanged();
                SyncRemoteCommand.NotifyCanExecuteChanged(); // the backup sync is import-gated too
                RepairRemoteCommand.NotifyCanExecuteChanged();
                ExportCommand.NotifyCanExecuteChanged(); // an export mid-import would snapshot a partial project
                // An external sync just finished → reload counts/covers. A Library-initiated import refreshes
                // itself afterwards (and owns the empty-board discard), so don't double-refresh for that case.
                if (!_importStatus.IsImporting && !_selfImporting) _ = RefreshAsync();
                break;
            case nameof(ImportStatus.IsRemoteSyncing):
                // This screen owns the flag today, and notifies these directly — but the gate reads the
                // SHARED status, so anything that ever sets it elsewhere must still grey the buttons out.
                ImportCommand.NotifyCanExecuteChanged();
                ExportCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    public void Dispose()
    {
        _disposed = true; // an in-flight RefreshAsync checks this on resume and applies nothing to the dead VM
        _importStatus.PropertyChanged -= OnImportStatusChanged;
        _disposeCts.Cancel(); // abort any in-flight import before it touches this disposed VM
        _disposeCts.Dispose();
        Tiles.DisposeAndClear(); // free every card's native cover bitmaps with the screen
    }

    // Design-time constructor for the XAML previewer.
    public LibraryViewModel() : this(null!, null!, null!, null!, null, new ToastService(), new ImportStatus(), _ => { }) { }

    /// <summary>"+ New board" tile followed by "All images" and one card per board (one ItemsControl flow).</summary>
    public ObservableCollection<ViewModelBase> Tiles { get; } = new();

    public string ProjectName => _projects?.Current?.Name ?? "";

    // ── Shell chrome (breadcrumb + floating bar) ─────────────────────────────

    /// <summary>The crumb carries the live match count while the board-name filter is active. The virtual
    /// "All images" card is exempt from the filter (a navigation staple, not a board), so it isn't counted.
    /// A "sync all" run takes it over: the per-board strips say what each board is doing, and this is the
    /// only place that can say how far through the <i>project</i> the run is (the grid has no top bar).</summary>
    public string CrumbTitle
    {
        get
        {
            if (SyncAllStatusText.Length > 0) return $"{ProjectName} ({SyncAllStatusText})";
            if (SearchText.Trim().Length == 0) return ProjectName;
            var n = Tiles.OfType<BoardCardRef>().Count(r => r.CollectionId is not null && !r.IsFilteredOut);
            return $"{ProjectName} ({(n == 1 ? "1 board" : $"{n} boards")} found)";
        }
    }

    /// <summary>The floating bar's ＋ menu for this screen.</summary>
    public IReadOnlyList<PlusAction> PlusActions { get; private set; } = Array.Empty<PlusAction>();

    /// <summary>The floating bar's search: a live, in-memory filter over the board cards by name (hidden via
    /// <see cref="BoardCardRef.IsFilteredOut"/>, never removed — no cover churn). Searching the project's
    /// IMAGES is deliberately not here: open "All images" and filter there instead.</summary>
    [ObservableProperty] private string _searchText = "";

    public string SearchPlaceholder => "Filter boards…";

    public void SubmitSearch() { } // live filter — Enter has nothing extra to do

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var q = SearchText.Trim();
        // "All images" (CollectionId null) stays visible through any filter — it's where a project-wide
        // image search happens, so the filter must never hide the way there.
        foreach (var r in Tiles.OfType<BoardCardRef>())
            r.IsFilteredOut = q.Length > 0 && r.CollectionId is not null
                              && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CrumbTitle)); // the crumb's "(x boards found)" tracks the filter
    }

    /// <summary>Revealed again after a board was popped: refresh the cards — a board's subtree count, size, and
    /// covers may have changed (folders created, sections imported, pins moved) while it was open.</summary>
    public void OnResumed() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed || _library is null || _projects?.Current is null) return;
        var collections = await _library.GetCollectionsAsync();
        if (_disposed) return; // popped while querying — don't refill a dead VM's tiles (stranded covers)

        Tiles.DisposeAndClear();
        Tiles.Add(NewBoardTile.Instance);
        Tiles.Add(new BoardCardRef(null, "All images", OpenBoardRef, edit: null)); // virtual board: no edit
        foreach (var c in collections)
        {
            var count = c.ItemCount == 1 ? "1 image" : $"{c.ItemCount} images";
            // Seed the importing strip if a sync/import is in flight for this board (e.g. rebuilt on return to the
            // Library while a board Sync is still running), so the card doesn't briefly drop its progress. A
            // "sync all" run's remaining boards are re-seeded the same way — the grid can be rebuilt from under
            // a run (returning from a board, a delete), and a card that lost its mark would look done.
            var importing = _importStatus.IsImporting && _importStatus.CollectionId == c.Id;
            var queued = !importing && _pendingSyncBoardIds.Contains(c.Id);
            Tiles.Add(new BoardCardRef(c.Id, c.Name, OpenBoardRef, BoardEditor.Begin)
            {
                MetaText = $"{count} · {ByteFormat.Format(c.SizeBytes)}",
                IsImporting = importing,
                IsQueued = queued,
                ImportStatusText = importing ? _importStatus.Text : queued ? QueuedText : "",
            });
        }

        // If the board Edit popup is open (e.g. an external sync just refreshed the grid under it), re-bind it to
        // the freshly-rebuilt card so a subsequent rename/delete acts on a card that's actually in Tiles, not the
        // discarded instance; close it if that board is gone.
        if (BoardEditor.IsSheetOpen && BoardEditor.EditTarget is { CollectionId: int editId })
        {
            var rebuilt = Tiles.OfType<BoardCardRef>().FirstOrDefault(r => r.CollectionId == editId);
            if (rebuilt is null) BoardEditor.IsSheetOpen = false;
            else BoardEditor.Begin(rebuilt);
        }

        ApplySearchFilter(); // rebuilt cards start unfiltered — reapply the bar's live query
        _ = LoadCoversAsync();
    }

    private void OpenBoardRef(BoardCardRef r) => _openBoard(new BoardTarget(r.CollectionId, r.Name));

    // ── Board Edit popup ──────────────────────────────────────────────────────
    // The rename / clear-cache / delete lifecycle lives in the shared BoardCardEditor (constructed in the ctor,
    // also used by the Board screen's folder cards). Only the board-specific source-merge actions stay here.

    /// <summary>The board pencil's Edit popup (shared with the Board screen's folder cards).</summary>
    public BoardCardEditor BoardEditor { get; }

    // The extra detail a board shows over a plain folder: its provenance line + the merged-source list.
    private Task LoadBoardSourcesAsync(BoardCardRef r, BoardDetail detail)
    {
        r.ImportedText = ImportedSummary(detail.Sources.Count);
        foreach (var s in detail.Sources)
        {
            var name = s.Name ?? s.SourceBoardId ?? DeriveBoardName(s.SourceUrl);
            r.Sources.Add(new Controls.BoardSourceRef(s.Id, name, s.SourceUrl, s.ImageCount));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove a merged source from a board (un-merge) and delete its images completely (files to the recycle
    /// bin), so the board keeps no orphaned pins. Routed through a confirmation in the view.
    /// </summary>
    public async Task RemoveSource(Controls.BoardSourceRef? source)
    {
        if (BoardEditor.EditTarget is not { CollectionId: int } r || source is null) return;
        try
        {
            var removed = await _curation.RemoveSourceAsync(source.Id);
            r.Sources.Remove(source);
            r.ImportedText = ImportedSummary(r.Sources.Count);
            _toasts.Show(removed > 0
                ? $"Removed source “{source.Name}” — {removed} image(s) {Services.RecycleWording.SentFate}."
                : $"Removed source “{source.Name}”.");
            await RefreshAsync(); // counts/covers change as images were removed
        }
        catch (Exception ex) { _toasts.Show($"Couldn't remove source: {ex.Message}", isError: true); }
    }

    /// <summary>The board's provenance line for the Edit popup, from how many sources it merges.</summary>
    private static string ImportedSummary(int sourceCount) => sourceCount switch
    {
        0 => "Local board",
        1 => "Imported from Pinterest",
        var n => $"Merged from {n} Pinterest boards",
    };

    /// <summary>"Add source board" — import another Pinterest board into this local board (a merge).</summary>
    public void AddSourceToEditTarget()
    {
        if (BoardEditor.EditTarget is not { CollectionId: int } r) return;
        BoardEditor.IsSheetOpen = false;
        OpenImportSheet();
        SelectedImportTarget = ImportTargets.FirstOrDefault(t => t.CollectionId == r.CollectionId) ?? SelectedImportTarget;
    }

    // Load each board card's 3-up collage covers (spread across the board) via the thumbnail cache, off the UI
    // thread — shared with the Board screen's folder cards.
    private Task LoadCoversAsync()
        => BoardCardCovers.LoadAsync(Tiles.OfType<BoardCardRef>().ToArray(), _library, _thumbnails);

    // ── Import ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isImportSheetOpen;
    [ObservableProperty] private string _boardUrl = "";
    [ObservableProperty] private string _cookiesBrowser = BrowserCookies.None;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _newBoardName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewBoardTarget))]
    private ImportTarget? _selectedImportTarget;

    public ObservableCollection<ImportTarget> ImportTargets { get; } = new();
    public IReadOnlyList<string> CookieBrowsers { get; } = BrowserCookies.Choices;

    /// <summary>True when the chosen target is a brand-new board (so the name field is shown).</summary>
    public bool IsNewBoardTarget => SelectedImportTarget is null || SelectedImportTarget.CollectionId is null;

    [RelayCommand]
    private void OpenImportSheet()
    {
        BoardUrl = "";
        NewBoardName = "";
        // Pre-select the user's default cookies browser (Settings), falling back to "(none)".
        CookiesBrowser = BrowserCookies.NormaliseChoice(_uiSettings?.Settings.DefaultCookiesBrowser);
        ImportTargets.Clear();
        ImportTargets.Add(new ImportTarget("＋  New board", null));
        foreach (var b in Tiles.OfType<BoardCardRef>().Where(r => r.CollectionId is not null))
            ImportTargets.Add(new ImportTarget(b.Name, b.CollectionId));
        SelectedImportTarget = ImportTargets[0];
        IsImportSheetOpen = true;
    }

    [RelayCommand]
    private void CloseImportSheet() => IsImportSheetOpen = false;

    // Default the new-board name from the URL's last path segment (the user can override it).
    partial void OnBoardUrlChanged(string value)
    {
        ImportCommand.NotifyCanExecuteChanged();
        if (IsNewBoardTarget) NewBoardName = DeriveBoardName(NormalizeUrl(value));
    }

    partial void OnIsImportingChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (_ingest is null || _projects?.Current is null) return;
        // A board Sync (or another import) may already own the single shared ImportStatus; refuse rather than
        // clobber its live count + streamed pins. CanImport also gates the button, but a sync can start after the
        // sheet was opened.
        if (_importStatus.IsImporting) { _toasts.Show("An import is already running — wait for it to finish."); return; }
        if (_importStatus.IsRemoteSyncing) { _toasts.Show("A backup sync is running — wait for it to finish."); return; }
        var token = _disposeCts.Token; // captured before any await — safe to read even after the CTS is disposed
        var url = NormalizeUrl(BoardUrl);

        var cookies = BrowserCookies.Resolve(CookiesBrowser);
        if (!cookies.Found) { _toasts.Show(cookies.Error!, isError: true); return; }
        _uiSettings?.RememberCookiesBrowser(CookiesBrowser); // the next sheet opens on it, here or on any board

        IsImportSheetOpen = false;
        IsImporting = true;

        // Resolve the target board UP FRONT — create the new board first so it appears immediately with a
        // progress strip and the pins have somewhere to land.
        var (card, targetId, isNew) = await ResolveImportTargetAsync(url);
        card.IsImporting = true;
        card.ImportStatusText = "Importing… starting";
        _selfImporting = true; // we drive this card + refresh ourselves; keep the watcher from doubling up
        _importStatus.Begin(targetId); // share with the Board screen

        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            DownloadArchivePath = _projects.DownloadArchivePathFor(_projects.Current),
        };

        var transcript = new StringBuilder();
        transcript.AppendLine($"Import {DateTime.Now:O}");
        transcript.AppendLine($"Project: {ProjectName} ({_projects.Current.Root})");
        transcript.AppendLine($"Board: {card.Name} (#{targetId})");
        transcript.AppendLine($"URL: {url}");
        transcript.AppendLine($"Cookies: {CookiesBrowser}");
        transcript.AppendLine(new string('-', 60));

        var progress = new Progress<IngestProgress>(p =>
        {
            if (p.Message is not null) transcript.AppendLine($"[{p.Phase}] {p.Message}");
            // No total is known mid-stream (gallery-dl doesn't report one), so show the live count.
            if (p.Phase is IngestPhase.Downloading or IngestPhase.Storing)
            {
                card.ImportStatusText = $"Importing… {p.Processed} so far";
                _importStatus.Text = card.ImportStatusText;
            }
            if (p.ImportedAsset is { } asset)
            {
                _importStatus.LastImportedCollectionId = p.ImportedIntoCollectionId; // set first — read on the pin change
                _importStatus.LastImported = asset; // stream into an open Board screen (its actual collection)
            }
        });

        try
        {
            var result = await _ingest.ImportAsync(url, options, progress, targetId, token);
            transcript.AppendLine($"RESULT: {result.NewAssets} new, {result.DuplicateAssets} duplicate.");
            _toasts.Show(result.TotalItems == 0
                ? $"“{card.Name}”: already up to date — nothing new."
                : $"Imported {result.NewAssets} new image(s) into “{card.Name}”.");
        }
        catch (OperationCanceledException)
        {
            // Navigated away from the Library mid-import (this VM disposed) — abort quietly, releasing the shared
            // status so the shell isn't left stuck "importing".
            _importStatus.End();
            _selfImporting = false;
            return;
        }
        catch (Exception ex)
        {
            transcript.AppendLine("EXCEPTION:");
            transcript.AppendLine(ex.ToString());
            var logPath = WriteImportLog(transcript);

            card.IsImporting = false;
            IsImporting = false;
            _importStatus.End();
            _selfImporting = false;

            // Discard a board this import just created if it landed nothing — don't leave an empty board behind.
            // A new board that DID get some pins before failing is kept (a partial import the user can finish via
            // Sync); an existing merge/sync target is never touched.
            var discarded = false;
            if (isNew && (await _library.GetBoardAssetShasAsync(targetId)).Count == 0)
            {
                try { await _curation.DeleteBoardAsync(targetId); } catch { /* best-effort cleanup */ }
                Tiles.Remove(card);
                card.Dispose(); // free its cover bitmaps with it
                discarded = true;
            }

            var msg = $"Import into “{card.Name}” failed: {ex.Message}".Trim();
            if (discarded) msg += " — the empty board was discarded.";
            if (logPath is not null) msg += $"  (log: {logPath})";
            _toasts.Show(msg, isError: true);
            return;
        }

        // Completed-just-before-Cancel race: if we were disposed during the await, don't mutate the dead VM.
        if (token.IsCancellationRequested) { _importStatus.End(); _selfImporting = false; return; }

        card.IsImporting = false;
        IsImporting = false;
        _importStatus.End();
        _selfImporting = false;
        await RefreshAsync();   // reload counts + covers (the importing card is rebuilt fresh)
        WriteImportLog(transcript);
    }

    // Create the new board (or locate the chosen existing one) and ensure it has a card in the grid.
    // IsNew distinguishes a board created for this import (safe to discard if the import fails empty) from an
    // existing board merged/synced into (never discarded).
    private async Task<(BoardCardRef Card, int TargetId, bool IsNew)> ResolveImportTargetAsync(string url)
    {
        if (SelectedImportTarget?.CollectionId is int existingId)
        {
            var existing = Tiles.OfType<BoardCardRef>().First(r => r.CollectionId == existingId);
            return (existing, existingId, false);
        }

        var name = string.IsNullOrWhiteSpace(NewBoardName) ? DeriveBoardName(url) : NewBoardName.Trim();
        var newId = await _ingest.CreateBoardAsync(name, url);
        // Wire the real Edit action from creation — the board exists in the DB now, so it must be editable/
        // deletable even with 0 images (e.g. when the import that created it then fails before the grid
        // refreshes). It was previously left null and only fixed by the post-import refresh, which a failed
        // import skips — leaving an empty board with no pencil.
        var card = new BoardCardRef(newId, name, OpenBoardRef, BoardEditor.Begin) { MetaText = "0 images" };
        Tiles.Insert(Math.Min(2, Tiles.Count), card); // after "+ New board" + "All images"
        return (card, newId, true);
    }

    // ── Sync every board in the project ────────────────────────────────────────────

    [ObservableProperty] private bool _isSyncAllSheetOpen;
    [ObservableProperty] private string _syncAllCookiesBrowser = BrowserCookies.None;

    private const string QueuedText = "Queued to sync";

    /// <summary>Boards a "sync all" run still has to reach. IDs, not cards: the grid is disposed and rebuilt
    /// on every <see cref="RefreshAsync"/> (returning from a board, a delete, an external import finishing),
    /// so a run holding card instances would be marking objects that have left the screen. Kept here so a
    /// rebuild mid-run can re-apply the marks.</summary>
    private readonly HashSet<int> _pendingSyncBoardIds = [];

    /// <summary>The live card for a board id, or null if the grid no longer has one.</summary>
    private BoardCardRef? Card(int collectionId)
        => Tiles.OfType<BoardCardRef>().FirstOrDefault(r => r.CollectionId == collectionId);

    private void ApplyQueuedMarks()
    {
        foreach (var id in _pendingSyncBoardIds)
        {
            if (Card(id) is not { IsImporting: false } c) continue;
            c.IsQueued = true;
            c.ImportStatusText = QueuedText;
        }
    }

    [RelayCommand]
    private void OpenSyncAllSheet()
    {
        SyncAllCookiesBrowser = BrowserCookies.NormaliseChoice(_uiSettings?.Settings.DefaultCookiesBrowser);
        IsSyncAllSheetOpen = true;
    }

    [RelayCommand]
    private void CloseSyncAllSheet() => IsSyncAllSheetOpen = false;

    /// <summary>
    /// Sync every board in the project, one after another — the same per-board delta the Board screen's Sync
    /// runs, so it stops at each target as soon as it reaches images already held. Sequential on purpose:
    /// they share one gallery-dl-shaped pipeline and one <see cref="ImportStatus"/>, and running boards
    /// concurrently would multiply the request rate at the source for no wall-clock certainty.
    /// <para>One board failing (a source gone private, a deleted board) <b>never</b> stops the rest — the
    /// whole point is not to have to visit boards one at a time — so failures are collected and reported at
    /// the end instead of aborting the run.</para>
    /// </summary>
    [RelayCommand]
    private async Task SyncAllAsync()
    {
        if (_projects.Current is null) return;
        if (_importStatus.IsImporting) { _toasts.Show("An import is already running — wait for it to finish."); return; }
        if (_importStatus.IsRemoteSyncing) { _toasts.Show("A backup sync is running — wait for it to finish."); return; }

        var cookies = BrowserCookies.Resolve(SyncAllCookiesBrowser);
        if (!cookies.Found) { _toasts.Show(cookies.Error!, isError: true); return; }
        _uiSettings?.RememberCookiesBrowser(SyncAllCookiesBrowser);

        var token = _disposeCts.Token; // captured before any await, like ImportAsync
        IsSyncAllSheetOpen = false;

        // Claim the run BEFORE the plan query: that query is an await, and until something is flagged the
        // Backup sheet's guard sees an idle app and would start replicating the very files this is about to
        // write. IsImporting is what CanSyncRemote (and CanImport) consult.
        IsImporting = true;
        IReadOnlyList<BoardSyncPlan> plans;
        try
        {
            plans = await _library.GetProjectSyncPlanAsync(token);
        }
        catch
        {
            IsImporting = false;
            throw;
        }
        if (plans.Count == 0)
        {
            IsImporting = false;
            _toasts.Show("No boards to sync — none of them have a source to re-fetch from.");
            return;
        }

        _selfImporting = true; // we drive the cards + the final refresh ourselves
        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            DownloadArchivePath = _projects.DownloadArchivePathFor(_projects.Current),
        };

        var transcript = new StringBuilder();
        transcript.AppendLine($"Sync all boards {DateTime.Now:O}");
        transcript.AppendLine($"Project: {ProjectName} ({_projects.Current.Root})");
        transcript.AppendLine($"Boards: {plans.Count}   Cookies: {SyncAllCookiesBrowser}");
        transcript.AppendLine(new string('-', 60));

        // `position` is where the run IS, `done` is how much of it worked — deliberately two counters. The
        // crumb once read its ordinal off `done`, so a board that failed left the next one still saying
        // "1 of 16": with no cookies every board 404s and the whole run counted itself as the first board.
        int position = 0, done = 0, newTotal = 0;
        // The message matters as much as the name: when every board fails it's one cause (a missing cookie
        // choice, a signed-out browser), and the run's closing toast is the only place that can say so.
        var failures = new List<(string Board, string Error)>();
        var cancelled = false;

        // Mark the whole queue up front, so the grid shows what this run is going to do rather than one card
        // lighting up at a time with no sign the rest are coming. The pending set is board IDS, never card
        // objects: RefreshAsync disposes and rebuilds every card (returning from a board, a delete), and a run
        // holding the old instances would be marking cards that are no longer on screen. Each board leaves the
        // set as its turn comes, so a card is only ever "queued" while it genuinely still has a turn to come.
        _pendingSyncBoardIds.Clear();
        foreach (var plan in plans) _pendingSyncBoardIds.Add(plan.CollectionId);
        ApplyQueuedMarks();

        try
        {
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                // Point the shared status at THIS board so its card shows the strip and an open Board screen
                // streams its pins — the same handover a single board's Sync does, once per board.
                _pendingSyncBoardIds.Remove(plan.CollectionId); // its turn came — no longer waiting
                var card = Card(plan.CollectionId);
                _importStatus.Begin(plan.CollectionId);
                if (card is not null)
                {
                    card.IsQueued = false; // the running bar replaces the still line
                    card.IsImporting = true;
                    card.ImportStatusText = "Syncing… starting";
                }
                SyncAllStatusText = $"syncing “{plan.Name}” ({++position} of {plans.Count})";
                transcript.AppendLine($"=== {plan.Name} (#{plan.CollectionId}) — {plan.Targets.Count} target(s)");

                // Per board, so a progress callback that lands after we've moved on can't write its count onto
                // the NEXT board's card (Progress<T> posts to the UI thread, so it always can). Both writes are
                // guarded: the card is re-resolved by id (the grid can be rebuilt mid-run), and the shared
                // status is only touched while it still points here — the watcher fans it out by CollectionId,
                // which would land a stale count on the next board's card just as surely.
                var boardId = plan.CollectionId;
                var progress = new Progress<IngestProgress>(p =>
                {
                    if (p.Message is not null) transcript.AppendLine($"[{p.Phase}] {p.Message}");
                    if (p.Phase is IngestPhase.Downloading or IngestPhase.Storing)
                    {
                        var text = $"Syncing… {p.Processed} so far";
                        if (Card(boardId) is { } live) live.ImportStatusText = text;
                        if (_importStatus.CollectionId == boardId) _importStatus.Text = text;
                    }
                    if (p.ImportedAsset is { } asset)
                    {
                        _importStatus.LastImportedCollectionId = p.ImportedIntoCollectionId; // set first — read on the pin change
                        _importStatus.LastImported = asset;
                    }
                });

                try
                {
                    var result = await _ingest.ImportAsync(
                        plan.Targets, options, progress, plan.CollectionId, ImportMode.Delta, token);
                    newTotal += result.NewAssets;
                    done++; // synced — a board that threw is counted as a failure, never as done
                    transcript.AppendLine($"RESULT: {result.NewAssets} new, {result.DuplicateAssets} already had.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw; // leaving the Library cancels the whole run, not just this board
                }
                catch (Exception ex)
                {
                    // The whole point is not having to visit boards one at a time, so one bad source (gone
                    // private, deleted) is collected and reported at the end, never a reason to stop.
                    failures.Add((plan.Name, ex.Message));
                    transcript.AppendLine($"FAILED: {ex}");
                }
                finally
                {
                    // Finished (or failed) — this board is neither running nor waiting, so its card drops the
                    // status line and goes back to showing its metadata. Re-resolved, not the instance captured
                    // above: a refresh during this board's crawl would have replaced it.
                    if (Card(boardId) is { } finished)
                    {
                        finished.IsImporting = false;
                        finished.IsQueued = false;
                        finished.ImportStatusText = "";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // navigated away from the Library mid-run
        }
        finally
        {
            SyncAllStatusText = "";
            // Whatever ended the run — finished, cancelled, or a throw the per-board catch didn't cover — no
            // board is queued any more. Left set, the boards it never reached would sit there claiming to be
            // waiting for a run that is over (and a later refresh would re-seed the mark from the stale set).
            _pendingSyncBoardIds.Clear();
            foreach (var c in Tiles.OfType<BoardCardRef>().Where(r => r.IsQueued))
            {
                c.IsQueued = false;
                c.ImportStatusText = "";
            }
            // End() BEFORE clearing _selfImporting: it flips the shared IsImporting, and the watcher refreshes
            // the grid unless this run still claims ownership — the ImportAsync ordering, for the same reason.
            _importStatus.End();
            IsImporting = false;
            _selfImporting = false;
        }

        WriteImportLog(transcript);
        if (cancelled)
        {
            // A re-run picks up from wherever each board stands, so say how far it got rather than letting the
            // strips just vanish (which reads as "finished"). The shell owns the toast host, so this shows
            // over whatever screen the user is on now. It carries whatever failed BEFORE the run was
            // abandoned, for the same reason the completed run does: those boards are exactly as broken as
            // they'd have been at the end, and dropping them here would make leaving the Library a way to
            // lose the report.
            var stopped = $"Sync stopped — {done} of {plans.Count} board(s) done before you left.";
            if (failures.Count > 0) stopped += $"  {failures.Count} failed so far.";
            _toasts.Show(stopped, isError: failures.Count > 0, details: SyncAllDetails(failures));
            return;
        }
        await RefreshAsync();

        _toasts.Show(SyncAllSummary(done, plans.Count, newTotal, failures),
            isError: failures.Count > 0, details: SyncAllDetails(failures));
    }

    /// <summary>
    /// The closing toast for a "sync all" run — the ONLY thing that reports what a multi-minute run achieved,
    /// since a board that fails just drops its progress strip and the crumb count clears.
    /// <para>Three cases, deliberately, because collapsing them hid a total failure: <b>nothing failed</b>
    /// reads as before; <b>some failed</b> says how many of how many worked and names the casualties;
    /// <b>everything failed</b> leads with the connector's own reason (the same actionable text a single
    /// board's Sync toasts — "the board is private, choose your cookies"). The old wording branched on
    /// <c>newTotal</c> alone, so 16 boards 404ing on a cookie-less run announced itself as "Synced 0 board(s)
    /// — already up to date", with the cause available only in the log.</para>
    /// </summary>
    internal static string SyncAllSummary(int done, int total, int newTotal,
        IReadOnlyList<(string Board, string Error)> failures)
    {
        if (failures.Count == 0)
            return newTotal == 0
                ? $"Synced {done} board(s) — already up to date."
                : $"Synced {done} board(s) — {newTotal} new image(s).";

        if (done == 0)
            return $"Sync failed — no board could be fetched. {Headline(failures[0].Error)}";

        var names = string.Join(", ", failures.Take(3).Select(f => f.Board)) + (failures.Count > 3 ? "…" : "");
        var got = newTotal == 0 ? "no new images" : $"{newTotal} new image(s)";
        // No "see the log": this toast carries SyncAllDetails, so every failure and its reason is one click
        // away on the card itself. Sending the user to find a file would be a step backwards from that.
        return $"Synced {done} of {total} board(s) — {got}.  {failures.Count} failed: {names}.";
    }

    /// <summary>
    /// The long form behind the closing toast's ⋯ button: every board that failed and what it said. The card
    /// can only carry the first reason and a count, and "see the log" asks the user to go find a file — a run
    /// where three of sixteen boards failed for three different reasons is only readable here.
    /// </summary>
    internal static string? SyncAllDetails(IReadOnlyList<(string Board, string Error)> failures)
    {
        if (failures.Count == 0) return null;
        var text = new StringBuilder();
        text.AppendLine(failures.Count == 1 ? "1 board failed." : $"{failures.Count} boards failed.");
        foreach (var (board, error) in failures)
        {
            text.AppendLine();
            text.AppendLine(board);
            text.AppendLine($"  {error}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The connector states the actionable cause in a sentence, then appends the raw gallery-dl tail
    /// (every target URL and its error line) — worth having in the transcript, unreadable in a toast that
    /// dismisses itself. Keep the sentence, drop the tail.</summary>
    private static string Headline(string message)
    {
        var cut = message.IndexOf(" [", StringComparison.Ordinal); // where the gallery-dl tail starts
        var head = (cut > 0 ? message[..cut] : message).TrimEnd();
        return head.Length <= 240 ? head : head[..240].TrimEnd() + "…";
    }

    /// <summary>How far a "sync all" run has got, surfaced through <see cref="CrumbTitle"/>.</summary>
    [ObservableProperty] private string _syncAllStatusText = "";

    partial void OnSyncAllStatusTextChanged(string value) => OnPropertyChanged(nameof(CrumbTitle));

    // Block while THIS grid is importing, and also while any other import/sync owns the single shared ImportStatus
    // (a board Sync sets only the shared flag, not this VM's IsImporting) — overlapping runs clobber its state.
    // A running remote (Backup) sync blocks too: an import writes the very files the replicator is copying.
    private bool CanImport() =>
        !IsImporting && !_importStatus.IsImporting && !_importStatus.IsRemoteSyncing
        && Uri.IsWellFormedUriString(NormalizeUrl(BoardUrl), UriKind.Absolute);

    /// <summary>Derive a default board name from a board URL's last path segment.</summary>
    private static string DeriveBoardName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var seg = uri.Segments.LastOrDefault(s => s.Trim('/').Length > 0)?.Trim('/');
            return string.IsNullOrWhiteSpace(seg) ? "New board" : Uri.UnescapeDataString(seg);
        }
        catch { return "New board"; }
    }

    /// <summary>Accept "pinterest.com/…" by prepending https:// when the user omits the scheme.</summary>
    private static string NormalizeUrl(string raw)
    {
        var url = raw.Trim();
        if (url.Length == 0) return url;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return url;
    }

    // ── Backup remote (SYNC-DESIGN P5/R2) ────────────────────────────────────
    // One remote per project, per machine (app-data project state). The sheet configures a folder
    // remote and runs RemoteSync (pull → apply → push); progress + result land on the status line.

    [ObservableProperty] private bool _isRemoteSheetOpen;
    [ObservableProperty] private string _remotePathText = "";
    [ObservableProperty] private string _remoteStatusText = "";
    [ObservableProperty] private bool _hasRemote;
    [ObservableProperty] private bool _isRemoteSyncing;

    private RemoteConfig? _remoteConfig;
    // True until this session has reconciled the whole file set with the configured folder — see
    // SetRemoteFolder. Deliberately not persisted: it only ever costs an extra thorough sync.
    private bool _remoteNeedsFullSync;

    [RelayCommand]
    private void OpenRemoteSheet()
    {
        if (_projects?.Current is not { } project) return;
        _remoteConfig = RemoteConfig.Load(_projects.AppPaths, project.Id);
        HasRemote = _remoteConfig is not null;
        RemotePathText = _remoteConfig?.Target ?? "";
        RemoteStatusText = "";
        IsRemoteSheetOpen = true;
        SyncRemoteCommand.NotifyCanExecuteChanged();
        RepairRemoteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void CloseRemoteSheet() => IsRemoteSheetOpen = false;

    /// <summary>The view's folder picker landed on a path — configure (or re-point) the remote.</summary>
    public void SetRemoteFolder(string path)
    {
        if (_projects?.Current is not { } project) return;
        var full = Path.GetFullPath(path);
        if (DestinationFolder.IsInsideProject(full, project.Root))
        {
            RemoteStatusText = "Choose a folder outside the project — an archive can't back up into itself.";
            return;
        }

        _remoteConfig = new RemoteConfig(RemoteConfig.FolderType, full);
        _remoteConfig.Save(_projects.AppPaths, project.Id);
        HasRemote = true;
        RemotePathText = full;
        RemoteStatusText = "";
        // A folder we've never synced with may hold a PARTIAL copy of this archive (a half-finished
        // rclone target, a drive someone tidied). Delta mode trusts what the remote already has, so give
        // this first run the full reconcile instead — after that the fast path is safe.
        _remoteNeedsFullSync = true;
        SyncRemoteCommand.NotifyCanExecuteChanged();
        RepairRemoteCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveRemote()
    {
        if (_projects?.Current is not { } project) return;
        RemoteConfig.Remove(_projects.AppPaths, project.Id);
        _remoteConfig = null;
        HasRemote = false;
        RemotePathText = "";
        RemoteStatusText = "";
        _remoteNeedsFullSync = false;
        SyncRemoteCommand.NotifyCanExecuteChanged();
        RepairRemoteCommand.NotifyCanExecuteChanged();
    }

    private bool CanSyncRemote() =>
        _remoteConfig is not null && !IsRemoteSyncing && !_importStatus.IsImporting
        && _dbFactory is not null && _archive is not null;

    /// <summary>The every-day sync: move only what the op log proves is new (see
    /// <see cref="ReplicationMode.Delta"/>), so it costs what changed rather than what the archive holds.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncRemote))]
    private Task SyncRemoteAsync()
        => RunRemoteAsync(_remoteNeedsFullSync ? ReplicationMode.Full : ReplicationMode.Delta);

    /// <summary>The thorough pass: re-check every file on both sides. Heals what a delta sync structurally
    /// cannot see — a backup someone deleted files from, a torn copy, images no op names.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncRemote))]
    private Task RepairRemoteAsync() => RunRemoteAsync(ReplicationMode.Full);

    private async Task RunRemoteAsync(ReplicationMode mode)
    {
        if (_projects?.Current is not { } project || _remoteConfig is null
            || _dbFactory is null || _archive is null || _importStatus.IsImporting) return;
        var token = _disposeCts.Token; // captured before any await — safe to read even after the CTS is disposed
        IsRemoteSyncing = true;
        _importStatus.IsRemoteSyncing = true; // published app-wide: imports/board-syncs refuse to overlap
        SyncRemoteCommand.NotifyCanExecuteChanged();
        RepairRemoteCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged(); // export refuses while a sync runs — grey it out, don't dead-click
        try
        {
            RemoteStatusText = mode == ReplicationMode.Full ? "Checking every file…" : "Checking the backup…";
            var progress = new Progress<string>(text => RemoteStatusText = text);
            var remote = _remoteConfig.CreateStore();
            var report = await Task.Run(
                () => RemoteSync.SyncAsync(project, remote, _dbFactory, _archive, mode: mode, progress: progress, ct: token),
                token);
            if (report.Verified) _remoteNeedsFullSync = false;
            RemoteStatusText = Summarise(report);
            if (report.ChaptersPulled > 0 && !_disposed) _ = RefreshAsync(); // changes arrived — reload the grid
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-sync; every replicator step is idempotent, the next sync resumes.
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Backup sync failed for project at {Root}", _projects.Current?.Root);
            RemoteStatusText = "Sync failed: " + ex.Message;
        }
        finally
        {
            IsRemoteSyncing = false;
            _importStatus.IsRemoteSyncing = false;
            SyncRemoteCommand.NotifyCanExecuteChanged();
            RepairRemoteCommand.NotifyCanExecuteChanged();
            ExportCommand.NotifyCanExecuteChanged(); // export refuses while a sync runs — show that
        }
    }

    private static string Summarise(ReplicationReport report)
    {
        var text = report.AnythingMoved ? "Done — " + string.Join(" · ", Legs(report)) + "." : NothingMoved(report);
        // Say so when the run knowingly left work behind — a backup that is quietly incomplete is worse
        // than a slow one.
        if (report.BlobsUnavailable > 0)
            text += $" {report.BlobsUnavailable} image(s) are missing from the backup — run Repair backup.";
        if (report.ChaptersDeferred > 0)
            text += " Some history couldn't be sent this time — it will go next sync.";
        return text;

        static string NothingMoved(ReplicationReport report) =>
            report.Verified ? "Backup verified — every file is already there." : "Already in sync — nothing to move.";

        static IEnumerable<string> Legs(ReplicationReport report)
        {
            if (report.BlobsPushed > 0 || report.ChaptersPushed > 0) yield return Leg("sent", report.BlobsPushed);
            if (report.BlobsPulled > 0 || report.ChaptersPulled > 0) yield return Leg("received", report.BlobsPulled);
        }

        static string Leg(string verb, int blobs) =>
            blobs > 0 ? $"{verb} {blobs} {(blobs == 1 ? "image" : "images")}" : $"{verb} history changes";
    }

    // ── Human-readable export: materialise the WHOLE project as a Project/Board/Folder/image tree ─
    // The board screen has the same sheet for one board; this run covers every board in one pass, so
    // progress counts the project and boards whose names sanitise alike disambiguate against each other.

    [ObservableProperty] private bool _isExportSheetOpen;
    [ObservableProperty] private string _exportPathText = "";
    [ObservableProperty] private string _exportStatusText = "";
    [ObservableProperty] private bool _isExporting;
    private string? _exportFolder;

    [RelayCommand]
    private void OpenExportSheet()
    {
        ExportStatusText = "";
        IsExportSheetOpen = true;
    }

    [RelayCommand]
    private void CloseExportSheet() => IsExportSheetOpen = false;

    /// <summary>Called from the view's folder picker (it needs the visual tree's TopLevel).</summary>
    public void SetExportFolder(string path)
    {
        var full = Path.GetFullPath(path);
        // Test the folder the run will CREATE, not the one that was picked: choosing the project's own
        // parent puts <chosen>/<project name> right back on the project folder.
        if (_projects?.Current is { } project
            && DestinationFolder.IsInsideProject(
                Path.Combine(full, ExportNames.ProjectFolderName(project.Name)), project.Root))
        {
            ExportStatusText = "That would export into the project folder itself — choose a folder outside it.";
            return;
        }

        _exportFolder = full;
        ExportPathText = full;
        ExportStatusText = "";
        ExportCommand.NotifyCanExecuteChanged();
    }

    // Reading the archive is safe alongside anything, but an import/backup-sync mid-run would export a
    // partial snapshot that looks complete — refuse to start until they finish (they don't wait for us:
    // export never writes the archive, so nothing gates on IsExporting).
    private bool CanExport() =>
        _exporter is not null && _exportFolder is not null && _projects?.Current is not null
        && !IsExporting && !IsRemoteSyncing && !_importStatus.IsImporting && !_importStatus.IsRemoteSyncing;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (_exporter is null || _exportFolder is not { } destination || _projects?.Current is not { } project) return;
        // A sync can start after the sheet opened. Say so — a button that just does nothing reads as broken.
        if (_importStatus.IsImporting || _importStatus.IsRemoteSyncing)
        {
            ExportStatusText = "Wait for the import or backup sync to finish — an export now would miss part of it.";
            return;
        }
        var ct = _disposeCts.Token; // captured before any await — safe to read even after the CTS is disposed
        IsExporting = true;
        ExportCommand.NotifyCanExecuteChanged();
        try
        {
            ExportStatusText = "Exporting…";
            var progress = new Progress<string>(text => ExportStatusText = text);
            var report = await Task.Run(
                () => _exporter.ExportProjectAsync(project.Name, destination, progress, ct), ct);
            ExportStatusText = ExportSummary.Describe(report);
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-export; whole files only (temp+rename), the next export resumes.
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Project export failed for {Root} to {Destination}", project.Root, destination);
            ExportStatusText = "Export failed: " + ex.Message;
        }
        finally
        {
            IsExporting = false;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private string? WriteImportLog(StringBuilder transcript)
    {
        try
        {
            if (_projects.Current is not { } project) return null;
            var logsRoot = _projects.LogsRootFor(project);
            Directory.CreateDirectory(logsRoot); // the app-data location isn't pre-created like the v1 folder was
            var path = Path.Combine(logsRoot, $"import-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, transcript.ToString());
            return path;
        }
        catch
        {
            return null; // logging must never break an import
        }
    }
}
