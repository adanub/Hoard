using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Desktop.Navigation;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Marker for the leading “+ New project” tile in the grid (rendered by its own data template).</summary>
public sealed class NewProjectTile : ViewModelBase
{
    public static readonly NewProjectTile Instance = new();
    private NewProjectTile() { }
}

/// <summary>
/// A recent project, shown as a <see cref="Controls.ProjectCard"/>: name, cache size, and a 3-up collage
/// cover built from the project's cached thumbnails (read straight off disk — no DB is opened). The Open/Edit
/// buttons surface as per-item commands that call back into the launcher. The Edit-popup info fields
/// (counts/boards/dates/on-disk) are filled lazily when the popup opens.
/// </summary>
public partial class RecentProjectRef : ViewModelBase, IDisposable
{
    /// <summary>The project folder. Mutable: a rename moves the folder on disk and re-points this.</summary>
    public string Path { get; set; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _cacheSizeText = "Loading…";
    [ObservableProperty] private Bitmap? _thumb0;
    [ObservableProperty] private Bitmap? _thumb1;
    [ObservableProperty] private Bitmap? _thumb2;

    /// <summary>True when the card doesn't match the floating bar's live project filter — hidden (collapsed)
    /// in the grid rather than removed, so its cover bitmaps aren't churned by typing.</summary>
    [ObservableProperty] private bool _isFilteredOut;

    // The covers are native (Skia) bitmaps — free the outgoing surface on every swap (rebuild, clear-cache, card
    // drop) rather than leaving it to lagging finalization; the eager-free rule from AssetTileViewModel.Thumbnail.
    partial void OnThumb0Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();
    partial void OnThumb1Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();
    partial void OnThumb2Changed(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    /// <summary>Set once the card leaves the grid (reload/forget/delete). The cover load checks it after its await
    /// so a decode finishing for a removed card frees its bitmaps instead of stranding them (same contract as
    /// <see cref="BoardCardRef.IsDisposed"/>).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Free the covers' native bitmaps but keep the card usable (clear-cache: the collage source is gone
    /// but the project card stays on the grid and reloads covers next reveal).</summary>
    public void ClearThumbs() => Thumb0 = Thumb1 = Thumb2 = null;

    /// <summary>The card left the grid — free its covers and refuse any late cover-load assignment.</summary>
    public void Dispose()
    {
        IsDisposed = true;
        ClearThumbs();
    }

    // Edit-popup info (lazy): live counts + boards come from the DB; dates + on-disk size from the folder.
    [ObservableProperty] private string _countsText = "";
    [ObservableProperty] private string _boardsText = "";
    [ObservableProperty] private string _onDiskText = "";
    [ObservableProperty] private string _addedText = "";
    [ObservableProperty] private string _modifiedText = "";

    /// <summary>Open this project (the card's Open button).</summary>
    public IRelayCommand OpenCommand { get; }
    /// <summary>Open this project's Edit popup (the card's Edit pencil).</summary>
    public IRelayCommand EditCommand { get; }

    public RecentProjectRef(string path, string name, Action<RecentProjectRef> open, Action<RecentProjectRef> edit)
    {
        Path = path;
        _name = name;
        OpenCommand = new RelayCommand(() => open(this));
        EditCommand = new RelayCommand(() => edit(this));
    }
}

/// <summary>
/// The Projects screen: a grid of project-board cards led by a “+ New project” tile. Opening a card (or
/// creating/adopting a project via the new-project sheet) raises a callback so the shell pushes the library.
/// Per-project management (open / clear cache / remove / delete) acts on a specific card; feedback is toasted.
/// </summary>
public partial class ProjectLauncherViewModel : ViewModelBase, IResumable, ICrumbTitled, IProvidesSearch, IProvidesPlusActions, IImmersivePage
{
    private readonly ProjectManager _projects;
    private readonly ProjectDbContextFactory _dbFactory;
    private readonly ToastService _toasts;
    private readonly Action _onProjectOpened;

    public ProjectLauncherViewModel(
        ProjectManager projects, ProjectDbContextFactory dbFactory, ToastService toasts, Action onProjectOpened)
    {
        _projects = projects;
        _dbFactory = dbFactory;
        _toasts = toasts;
        _onProjectOpened = onProjectOpened;
        PlusActions = new[] { new PlusAction("New project", OpenNewProjectSheetCommand) };

        Tiles.Add(NewProjectTile.Instance);
        if (projects is not null) ReloadRecents(); // skip in the design-time (null) ctor
    }

    // Design-time constructor for the XAML previewer.
    public ProjectLauncherViewModel() : this(null!, null!, new ToastService(), () => { }) { }

    /// <summary>Revealed again after a project's Library was popped back to Projects (the mouse/⌫ back): reload the
    /// recents — the just-used project floats to the top, a cache size may have grown, one may have been removed.</summary>
    public void OnResumed() => ReloadRecents();

    // Rebuild the recent-project cards from the current recents list (the leading "+ New project" tile stays put),
    // then refresh their cache sizes + collage covers off the UI thread.
    private void ReloadRecents()
    {
        for (var i = Tiles.Count - 1; i >= 0; i--)
            if (Tiles[i] is RecentProjectRef dropped)
            {
                Tiles.RemoveAt(i);
                dropped.Dispose(); // free covers + mark so a still-in-flight cover load can't re-pin bitmaps on it
            }
        foreach (var path in _projects.RecentProjects)
            Tiles.Add(NewRef(path, new DirectoryInfo(path).Name));
        ApplySearchFilter(); // rebuilt cards start unfiltered — reapply the bar's live query
        _ = LoadRecentsAsync();
    }

    /// <summary>Build a recent-project card wired to this launcher's Open/Edit actions.</summary>
    private RecentProjectRef NewRef(string path, string name)
        => new(path, name, r => _ = OpenProjectAsync(r), BeginEdit);

    /// <summary>The “+ New” tile followed by one card per recent project (both rendered in the same grid).</summary>
    public ObservableCollection<ViewModelBase> Tiles { get; } = new();

    private IEnumerable<RecentProjectRef> Recents => Tiles.OfType<RecentProjectRef>();

    // ── Shell chrome (breadcrumb + floating bar) ─────────────────────────────

    /// <summary>The crumb carries the live match count while the project filter is active.</summary>
    public string CrumbTitle
    {
        get
        {
            if (SearchText.Trim().Length == 0) return "Projects";
            var n = Recents.Count(r => !r.IsFilteredOut);
            return $"Projects ({(n == 1 ? "1 project" : $"{n} projects")} found)";
        }
    }

    /// <summary>The floating bar's ＋ menu for this screen.</summary>
    public IReadOnlyList<PlusAction> PlusActions { get; }

    /// <summary>The floating bar's search: a live, in-memory filter over the recent-project cards (hidden via
    /// <see cref="RecentProjectRef.IsFilteredOut"/>, never removed — no cover-bitmap churn while typing).</summary>
    [ObservableProperty] private string _searchText = "";

    public string SearchPlaceholder => "Filter projects…";

    public void SubmitSearch() { } // live filter — Enter has nothing extra to do

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        var q = SearchText.Trim();
        foreach (var r in Recents)
            r.IsFilteredOut = q.Length > 0 && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(CrumbTitle)); // the crumb's "(x projects found)" tracks the filter
    }

    // ── New-project sheet ────────────────────────────────────────────────────

    [ObservableProperty] private bool _isNewProjectSheetOpen;
    [ObservableProperty] private string _newProjectName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    private string _newProjectLocation = "";

    /// <summary>An error from creating/opening shown inside the sheet (distinct from the live name validation).</summary>
    [ObservableProperty] private string? _sheetError;

    [RelayCommand]
    private void OpenNewProjectSheet()
    {
        NewProjectName = "";
        NewProjectLocation = "";
        SheetError = null;
        IsNewProjectSheetOpen = true;
    }

    [RelayCommand]
    private void CloseNewProjectSheet() => IsNewProjectSheetOpen = false;

    /// <summary>A name validation message to show under the field, or null when the name is fine.</summary>
    public string? NameError => string.IsNullOrEmpty(NewProjectName) ? null : HoardProject.ValidateName(NewProjectName);
    public bool HasNameError => NameError is not null;

    /// <summary>The folder that would be created, shown to the user before they commit.</summary>
    public string NewProjectPreviewPath =>
        !string.IsNullOrWhiteSpace(NewProjectLocation) && CanCreate()
            ? Path.Combine(NewProjectLocation, NewProjectName.Trim())
            : "";

    partial void OnNewProjectNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(HasNameError));
        OnPropertyChanged(nameof(NewProjectPreviewPath));
        CreateProjectCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewProjectLocationChanged(string value)
        => OnPropertyChanged(nameof(NewProjectPreviewPath));

    /// <summary>Called by the view's folder picker to set the parent location for a new project.</summary>
    public void SetNewProjectLocation(string parentFolder) => NewProjectLocation = parentFolder;

    // While a project is being opened/created its database is built and the EF model is compiled (a one-off,
    // CPU-heavy first-use cost) — done off the UI thread behind this flag so the shell shows a spinner instead
    // of freezing. See OpenOffUiThreadAsync.
    [ObservableProperty] private bool _isOpening;

    /// <summary>The busy overlay (project opening/creating) is a page-LOCAL scrim, so it can't cover the
    /// shell's floating bar — surfacing it as "immersive" hides the bar (and gates crumb clicks) for the
    /// duration, otherwise ＋ → New project could start a second concurrent open over the first.</summary>
    public bool IsImmersive => IsOpening;

    partial void OnIsOpeningChanged(bool value) => OnPropertyChanged(nameof(IsImmersive));
    [ObservableProperty] private string _openingText = "Opening project…";

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateProjectAsync()
    {
        try
        {
            var folder = Path.Combine(NewProjectLocation, NewProjectName.Trim());
            // Keep the sheet open (under the busy overlay) until the work succeeds, so a failure shows the
            // error in place instead of the sheet flashing closed then reopening.
            await OpenOffUiThreadAsync($"Creating “{NewProjectName.Trim()}”…", () =>
            {
                _projects.Create(folder, NewProjectName.Trim());
                return _dbFactory.EnsureCreatedAsync();
            });
            IsNewProjectSheetOpen = false;
            _onProjectOpened();
        }
        catch (Exception ex)
        {
            SheetError = "Couldn't create project: " + ex.Message;
        }
    }

    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(NewProjectLocation) && HoardProject.ValidateName(NewProjectName) is null;

    /// <summary>Open an existing project folder chosen via the sheet's folder picker.</summary>
    public async Task OpenExistingAsync(string folder)
    {
        if (OfferMigrationIfLegacy(folder, new DirectoryInfo(folder).Name)) return;
        var err = await TryOpenAsync(folder);
        if (err is null) IsNewProjectSheetOpen = false;
        else SheetError = "Couldn't open project: " + err;
    }

    /// <summary>
    /// Raised when an open lands on a legacy-format (v1) project: the view shows the one-time
    /// storage-upgrade confirm and finishes via <see cref="FinishOpenAsync"/> with the user's answer.
    /// With no listener the open just proceeds in the legacy layout, as before.
    /// </summary>
    public event Action<string, string>? MigrationOfferRequested;

    private bool OfferMigrationIfLegacy(string folder, string name)
    {
        try
        {
            if (MigrationOfferRequested is null || !HoardProject.IsProject(folder)) return false;
            var (format, _) = HoardProject.Peek(folder);
            if (format >= HoardProject.CurrentFormatVersion) return false;
            if (!File.Exists(Path.Combine(folder, "hoard.db"))) return false;
        }
        catch
        {
            return false; // let the normal open path surface the real error
        }
        MigrationOfferRequested.Invoke(folder, name);
        return true;
    }

    /// <summary>The migration confirm's outcome: upgrade the storage format (Upgrade) or keep the legacy
    /// layout for now (Cancel) — the project opens either way.</summary>
    public async Task FinishOpenAsync(string folder, string name, bool migrate)
    {
        var err = await TryOpenAsync(folder, migrate);
        if (err is null) IsNewProjectSheetOpen = false;
        else _toasts.Show($"Couldn't open “{name}”: {err}", isError: true);
    }

    /// <summary>Adopt a folder that holds project data but has lost/altered its marker (rewrite the marker and
    /// open it). The view offers this only after confirming a marker-less but project-shaped folder.</summary>
    public async Task AdoptExistingAsync(string folder)
    {
        try
        {
            await OpenOffUiThreadAsync("Adopting project…", () =>
            {
                _projects.Adopt(folder);
                return _dbFactory.EnsureCreatedAsync();
            });
            IsNewProjectSheetOpen = false;
            _onProjectOpened();
        }
        catch (Exception ex)
        {
            SheetError = "Couldn't adopt project: " + ex.Message;
        }
    }

    /// <summary>Surface a message in the new-project sheet (e.g. a folder that isn't a project at all).</summary>
    public void ShowSheetError(string message) => SheetError = message;

    // ── Open project (the card's Open button) ────────────────────────────────

    /// <summary>Open the project behind a card (the card's Open button calls this).</summary>
    public async Task OpenProjectAsync(RecentProjectRef r)
    {
        if (OfferMigrationIfLegacy(r.Path, r.Name)) return;
        var err = await TryOpenAsync(r.Path);
        if (err is not null) _toasts.Show($"Couldn't open “{r.Name}”: {err}", isError: true);
    }

    private async Task<string?> TryOpenAsync(string folder, bool migrate = false)
    {
        try
        {
            await OpenOffUiThreadAsync(migrate ? "Upgrading project storage…" : "Opening project…", () =>
            {
                _projects.Open(folder);
                return _dbFactory.EnsureCreatedAsync(upgradeLegacyFormat: migrate);
            });
            _onProjectOpened();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Run a project open/create (open the folder + build/upgrade its DB) on a background thread, with the
    /// busy spinner shown. The DB work compiles the EF model on first use — a synchronous, one-off cost that
    /// would otherwise stall the UI thread — so it must not run inline. Navigation happens after, back on the
    /// UI thread (the await resumes there).
    /// </summary>
    private async Task OpenOffUiThreadAsync(string busyText, Func<Task> work)
    {
        OpeningText = busyText;
        IsOpening = true;
        try
        {
            await Task.Run(work);
        }
        finally
        {
            IsOpening = false;
        }
    }

    // ── Edit popup (per-project info + rename) ───────────────────────────────

    /// <summary>The project whose Edit popup is open (null when closed). Drives the popup's bindings.</summary>
    [ObservableProperty] private RecentProjectRef? _editTarget;
    [ObservableProperty] private bool _isEditSheetOpen;

    [RelayCommand]
    private void CloseEditSheet() => IsEditSheetOpen = false;

    /// <summary>Open the Edit popup for a project and (lazily) fill its info rows.</summary>
    public void BeginEdit(RecentProjectRef r)
    {
        EditTarget = r;
        IsEditSheetOpen = true;
        _ = LoadEditInfoAsync(r);
    }

    private async Task LoadEditInfoAsync(RecentProjectRef r)
    {
        // Cheap folder-derived info first (dates), then the on-disk size + DB counts off the UI thread.
        var info = new DirectoryInfo(r.Path);
        var exists = info.Exists;
        r.AddedText = "Added " + (exists ? info.CreationTime.ToString("d MMM yyyy") : "—");
        r.ModifiedText = "Updated " + (exists ? info.LastWriteTime.ToString("d MMM yyyy") : "—");
        r.CountsText = "Counting…";
        r.BoardsText = "";

        var onDisk = await Task.Run(() => DirectorySize(r.Path));
        r.OnDiskText = ByteFormat.Format(onDisk) + " on disk";

        try
        {
            var stats = await ProjectStatsReader.ReadAsync(r.Path, _projects.AppPaths);
            r.CountsText = $"{stats.Images} images · {stats.Gifs} GIFs · {stats.Videos} videos";
            r.BoardsText = stats.Boards == 1 ? "1 board" : $"{stats.Boards} boards";
        }
        catch
        {
            r.CountsText = "Couldn't read project stats.";
            r.BoardsText = "";
        }
    }

    /// <summary>Rename a project: rename its folder on disk and re-point the card. From the Edit popup.</summary>
    public void RenameEditTarget(string? newName)
    {
        if (EditTarget is not { } r || string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            r.Path = _projects.RenameProject(r.Path, newName.Trim());
            r.Name = newName.Trim();
            _toasts.Show($"Renamed to “{r.Name}”.");
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't rename: {ex.Message}", isError: true);
        }
    }

    // ── Per-project actions ──────────────────────────────────────────────────

    /// <summary>Clear a project's thumbnail cache (regenerated on demand, so this is safe).</summary>
    public async Task ClearCacheAsync(RecentProjectRef r)
    {
        var dir = HoardProject.ThumbnailsDir(r.Path);
        var freed = await Task.Run(() =>
        {
            var size = DirectorySize(dir);
            ClearDirectory(dir);
            return size;
        });
        r.CacheSizeText = ByteFormat.Format(0) + " cache";
        r.ClearThumbs(); // the collage came from those thumbnails (the card itself stays on the grid)
        _toasts.Show($"Cleared {ByteFormat.Format(freed)} of thumbnails for “{r.Name}” (they rebuild as you browse).");
    }

    /// <summary>Remove a project from the list without deleting any files.</summary>
    public void Forget(RecentProjectRef r)
    {
        _projects.RemoveFromRecents(r.Path);
        Tiles.Remove(r);
        r.Dispose(); // free covers + block any in-flight cover load from re-pinning bitmaps on the removed card
        _toasts.Show($"Removed “{r.Name}” from the list (files left untouched).");
    }

    /// <summary>Permanently delete a project's folder. Destructive — the view confirms first.</summary>
    public void DeleteFromDisk(RecentProjectRef r)
    {
        try
        {
            _projects.DeleteProject(r.Path);
            Tiles.Remove(r);
            r.Dispose(); // free covers + block any in-flight cover load from re-pinning bitmaps on the removed card
            _toasts.Show($"Deleted “{r.Name}” and all its data.");
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't delete “{r.Name}”: {ex.Message}", isError: true);
        }
    }

    // ── Loading recents (cache size + collage thumbnails), off the UI thread ──

    private async Task LoadRecentsAsync()
    {
        foreach (var r in Recents.ToArray())
        {
            var (size, thumbs) = await Task.Run(() =>
            {
                var dir = HoardProject.ThumbnailsDir(r.Path);
                var sz = DirectorySize(dir);
                var files = PickSpreadThumbnails(dir, 3);
                return (sz, files.Select(LoadThumbnail).ToList());
            });

            // Back on the UI thread (the await resumed here): publish to the card — unless a newer ReloadRecents
            // (every launcher reveal) or a Forget/Delete removed it while we were decoding, in which case assigning
            // would strand the fresh bitmaps on a card that's out of the grid with no disposal path left.
            if (r.IsDisposed) { foreach (var t in thumbs) t?.Dispose(); continue; }
            r.CacheSizeText = ByteFormat.Format(size) + " cache";
            if (thumbs.Count > 0) r.Thumb0 = thumbs[0];
            if (thumbs.Count > 1) r.Thumb1 = thumbs[1];
            if (thumbs.Count > 2) r.Thumb2 = thumbs[2];
        }
    }

    // Pick `count` cached thumbnails spread across the browse history (newest, midpoint, oldest by file time) so
    // the project collage shows variety rather than the first three files (which cluster on one board). The
    // project isn't open here, so there's no DB to ask for boards/recency — file time is the available proxy.
    private static List<string> PickSpreadThumbnails(string dir, int count)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        var files = new DirectoryInfo(dir).EnumerateFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToList();
        return SpreadSelect.Positions(files.Count, count).Select(i => files[i]).ToList();
    }

    private static Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 240); // card width; the cached PNGs may be larger
        }
        catch
        {
            return null; // a missing/corrupt thumbnail just leaves a placeholder tile
        }
    }

    private static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        // Enumerate FileInfo directly: on Windows the length is already populated, avoiding a second stat.
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; } catch { /* skip files that vanish mid-scan */ }
        }
        return total;
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch { /* best-effort */ }
        }
    }
}
