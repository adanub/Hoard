using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Desktop.Services;
using Hoard.Ingest.GalleryDl;

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
public partial class BoardCardRef : ViewModelBase
{
    public int? CollectionId { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private Bitmap? _thumb0;
    [ObservableProperty] private Bitmap? _thumb1;
    [ObservableProperty] private Bitmap? _thumb2;

    // While this board is being imported into, the card shows a pinned progress strip + live count.
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatusText = "";

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
/// The Library screen: a grid of the project's boards (as cards) led by "+ New board" and "All images", with a
/// project-wide search and an Import action. Opening a card (or searching) pushes the Board screen; the back
/// chevron returns to Projects (switching project). Per-board editing/merge arrives with the board model.
/// </summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly IngestService _ingest;
    private readonly LibraryService _library;
    private readonly CurationService _curation;
    private readonly ProjectManager _projects;
    private readonly ThumbnailCache? _thumbnails;
    private readonly ToastService _toasts;
    private readonly ImportStatus _importStatus;
    private readonly Action<BoardTarget> _openBoard;
    private readonly Action _requestSwitchProject;

    public LibraryViewModel(
        IngestService ingest, LibraryService library, CurationService curation, ProjectManager projects,
        ThumbnailCache? thumbnails, ToastService toasts, ImportStatus importStatus,
        Action<BoardTarget> openBoard, Action requestSwitchProject)
    {
        _ingest = ingest;
        _library = library;
        _curation = curation;
        _projects = projects;
        _thumbnails = thumbnails;
        _toasts = toasts;
        _importStatus = importStatus;
        _openBoard = openBoard;
        _requestSwitchProject = requestSwitchProject;
        _ = RefreshAsync();
    }

    // Design-time constructor for the XAML previewer.
    public LibraryViewModel() : this(null!, null!, null!, null!, null, new ToastService(), new ImportStatus(), _ => { }, () => { }) { }

    /// <summary>"+ New board" tile followed by "All images" and one card per board (one ItemsControl flow).</summary>
    public ObservableCollection<ViewModelBase> Tiles { get; } = new();

    public string ProjectName => _projects?.Current?.Name ?? "";

    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void Back() => _requestSwitchProject();

    /// <summary>Run the project-wide search: open the Board screen over "All images" filtered to the query.</summary>
    [RelayCommand]
    private void Search()
    {
        var q = SearchQuery.Trim();
        if (q.Length > 0) _openBoard(new BoardTarget(null, $"Results for “{q}”", q));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_library is null || _projects?.Current is null) return;

        Tiles.Clear();
        Tiles.Add(NewBoardTile.Instance);
        Tiles.Add(new BoardCardRef(null, "All images", OpenBoardRef, edit: null)); // virtual board: no edit
        foreach (var c in await _library.GetCollectionsAsync())
        {
            var count = c.ItemCount == 1 ? "1 image" : $"{c.ItemCount} images";
            Tiles.Add(new BoardCardRef(c.Id, c.Name, OpenBoardRef, BeginEditBoard)
            {
                MetaText = $"{count} · {ByteFormat.Format(c.SizeBytes)}",
            });
        }

        _ = LoadCoversAsync();
    }

    private void OpenBoardRef(BoardCardRef r) => _openBoard(new BoardTarget(r.CollectionId, r.Name));

    // ── Board Edit popup ──────────────────────────────────────────────────────

    [ObservableProperty] private BoardCardRef? _boardEditTarget;
    [ObservableProperty] private bool _isBoardEditSheetOpen;

    [RelayCommand]
    private void CloseBoardEditSheet() => IsBoardEditSheetOpen = false;

    /// <summary>Open a board's Edit popup (the pencil) and fill its detail rows + source list lazily.</summary>
    public void BeginEditBoard(BoardCardRef r)
    {
        BoardEditTarget = r;
        IsBoardEditSheetOpen = true;
        _ = LoadBoardDetailAsync(r);
    }

    private async Task LoadBoardDetailAsync(BoardCardRef r)
    {
        if (r.CollectionId is not int id) return;
        r.CountsText = "Counting…";
        r.CacheText = "";
        r.Sources.Clear();

        try
        {
            var detail = await _library.GetBoardDetailAsync(id);
            if (detail is null) { r.CountsText = "No details."; return; }
            r.CountsText = $"{detail.Images} images · {detail.Gifs} GIFs · {detail.Videos} videos";
            r.CacheText = ByteFormat.Format(detail.SizeBytes) + " on disk";
            r.AddedText = "Added " + detail.CreatedAt.LocalDateTime.ToString("d MMM yyyy");
            r.ImportedText = ImportedSummary(detail.Sources.Count);
            foreach (var s in detail.Sources)
            {
                var name = s.Name ?? s.SourceBoardId ?? DeriveBoardName(s.SourceUrl);
                r.Sources.Add(new Controls.BoardSourceRef(s.Id, name, s.SourceUrl, s.ImageCount));
            }
        }
        catch (Exception ex)
        {
            // Don't leave the popup stuck on "Counting…" if the read fails — say so and surface the reason.
            r.CountsText = "Couldn't load details.";
            _toasts.Show($"Couldn't load board details: {ex.Message}", isError: true);
        }
    }

    /// <summary>Rename a board (its local name).</summary>
    public async Task RenameBoardAsync(string? newName)
    {
        if (BoardEditTarget is not { CollectionId: int id } r || string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            await _curation.RenameBoardAsync(id, newName.Trim());
            r.Name = newName.Trim();
            _toasts.Show($"Renamed to “{r.Name}”.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't rename: {ex.Message}", isError: true); }
    }

    /// <summary>Clear a board's cached thumbnails (regenerated on demand).</summary>
    public async Task ClearBoardCacheAsync()
    {
        if (BoardEditTarget is not { CollectionId: int id } r) return;
        var shas = await _library.GetBoardAssetShasAsync(id);
        if (_thumbnails is not null)
            foreach (var sha in shas) _thumbnails.Evict(sha);
        _toasts.Show($"Cleared cached thumbnails for “{r.Name}”.");
    }

    /// <summary>Delete a board and its images completely (files to the recycle bin).</summary>
    public async Task DeleteBoardAsync()
    {
        if (BoardEditTarget is not { CollectionId: int id } r) return;
        try
        {
            var removed = await _curation.DeleteBoardAsync(id);
            Tiles.Remove(r);
            _toasts.Show($"Deleted board “{r.Name}” — {removed} image(s) sent to the recycle bin.");
        }
        catch (Exception ex) { _toasts.Show($"Couldn't delete board: {ex.Message}", isError: true); }
    }

    /// <summary>
    /// Remove a merged source from a board (un-merge) and delete its images completely (files to the recycle
    /// bin), so the board keeps no orphaned pins. Routed through a confirmation in the view.
    /// </summary>
    public async Task RemoveSource(Controls.BoardSourceRef? source)
    {
        if (BoardEditTarget is not { CollectionId: int } r || source is null) return;
        try
        {
            var removed = await _curation.RemoveSourceAsync(source.Id);
            r.Sources.Remove(source);
            r.ImportedText = ImportedSummary(r.Sources.Count);
            _toasts.Show(removed > 0
                ? $"Removed source “{source.Name}” — {removed} image(s) sent to the recycle bin."
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
        if (BoardEditTarget is not { CollectionId: int } r) return;
        IsBoardEditSheetOpen = false;
        OpenImportSheet();
        SelectedImportTarget = ImportTargets.FirstOrDefault(t => t.CollectionId == r.CollectionId) ?? SelectedImportTarget;
    }

    // Load each board card's 3-up collage covers (newest images) via the thumbnail cache, off the UI thread.
    // Per-board try/catch so one bad board (e.g. a missing blob) can't abort covers for all the others.
    private async Task LoadCoversAsync()
    {
        foreach (var r in Tiles.OfType<BoardCardRef>().ToArray())
        {
            try
            {
                var covers = await _library.GetCoverAssetsAsync(r.CollectionId, 3);
                for (var i = 0; i < covers.Count; i++)
                {
                    var bmp = _thumbnails is not null
                        ? await _thumbnails.GetAsync(covers[i].Sha256, covers[i].AbsolutePath, 240)
                        : null;
                    if (i == 0) r.Thumb0 = bmp;
                    else if (i == 1) r.Thumb1 = bmp;
                    else r.Thumb2 = bmp;
                }
            }
            catch
            {
                // A board whose covers can't load just keeps its placeholder tiles.
            }
        }
    }

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
        var url = NormalizeUrl(BoardUrl);

        var cookies = BrowserCookies.Resolve(CookiesBrowser);
        if (!cookies.Found) { _toasts.Show(cookies.Error!, isError: true); return; }

        IsImportSheetOpen = false;
        IsImporting = true;

        // Resolve the target board UP FRONT — create the new board first so it appears immediately with a
        // progress strip and the pins have somewhere to land.
        var (card, targetId) = await ResolveImportTargetAsync(url);
        card.IsImporting = true;
        card.ImportStatusText = "Importing… starting";
        _importStatus.Begin(targetId); // share with the Board screen

        var options = new ConnectorOptions
        {
            CookiesFromBrowser = cookies.Spec,
            DownloadArchivePath = _projects.Current.DownloadArchivePath,
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
            if (p.ImportedAsset is { } asset) _importStatus.LastImported = asset; // stream into an open Board screen
        });

        try
        {
            var result = await _ingest.ImportAsync(url, options, progress, targetId);
            transcript.AppendLine($"RESULT: {result.NewAssets} new, {result.DuplicateAssets} duplicate.");
            _toasts.Show(result.TotalItems == 0
                ? $"“{card.Name}”: already up to date — nothing new."
                : $"Imported {result.NewAssets} new image(s) into “{card.Name}”.");
        }
        catch (Exception ex)
        {
            transcript.AppendLine("EXCEPTION:");
            transcript.AppendLine(ex.ToString());
            var logPath = WriteImportLog(transcript);
            var msg = $"Import into “{card.Name}” failed: {ex.Message}".Trim();
            if (logPath is not null) msg += $"  (log: {logPath})";
            _toasts.Show(msg, isError: true);
            card.IsImporting = false;
            IsImporting = false;
            _importStatus.End();
            return;
        }

        card.IsImporting = false;
        IsImporting = false;
        _importStatus.End();
        await RefreshAsync();   // reload counts + covers (the importing card is rebuilt fresh)
        WriteImportLog(transcript);
    }

    // Create the new board (or locate the chosen existing one) and ensure it has a card in the grid.
    private async Task<(BoardCardRef Card, int TargetId)> ResolveImportTargetAsync(string url)
    {
        if (SelectedImportTarget?.CollectionId is int existingId)
        {
            var existing = Tiles.OfType<BoardCardRef>().First(r => r.CollectionId == existingId);
            return (existing, existingId);
        }

        var name = string.IsNullOrWhiteSpace(NewBoardName) ? DeriveBoardName(url) : NewBoardName.Trim();
        var newId = await _ingest.CreateBoardAsync(name, url);
        var card = new BoardCardRef(newId, name, OpenBoardRef, edit: null) { MetaText = "0 images" };
        Tiles.Insert(Math.Min(2, Tiles.Count), card); // after "+ New board" + "All images"
        return (card, newId);
    }

    private bool CanImport() => !IsImporting && Uri.IsWellFormedUriString(NormalizeUrl(BoardUrl), UriKind.Absolute);

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

    private string? WriteImportLog(StringBuilder transcript)
    {
        try
        {
            var logsRoot = _projects.Current?.LogsRoot;
            if (logsRoot is null) return null;
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
