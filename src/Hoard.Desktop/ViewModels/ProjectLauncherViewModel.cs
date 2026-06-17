using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Projects;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>Marker for the leading “+ New project” tile in the grid (rendered by its own data template).</summary>
public sealed class NewProjectTile : ViewModelBase
{
    public static readonly NewProjectTile Instance = new();
    private NewProjectTile() { }
}

/// <summary>
/// A recent project, shown as a board card: name, cache size, and a 3-up collage cover built from the
/// project's cached thumbnails (read straight off disk — no DB is opened).
/// </summary>
public partial class RecentProjectRef : ViewModelBase
{
    public string Path { get; }
    public string Name { get; }

    [ObservableProperty] private string _cacheSizeText = "Loading…";
    [ObservableProperty] private Bitmap? _thumb0;
    [ObservableProperty] private Bitmap? _thumb1;
    [ObservableProperty] private Bitmap? _thumb2;

    public RecentProjectRef(string path, string name)
    {
        Path = path;
        Name = name;
    }
}

/// <summary>
/// The Projects screen: a grid of project-board cards led by a “+ New project” tile. Opening a card (or
/// creating/adopting a project via the new-project sheet) raises a callback so the shell pushes the library.
/// Per-project management (open / clear cache / remove / delete) acts on a specific card; feedback is toasted.
/// </summary>
public partial class ProjectLauncherViewModel : ViewModelBase
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

        Tiles.Add(NewProjectTile.Instance);
        if (projects is not null) // skip in the design-time (null) ctor
        {
            foreach (var path in projects.RecentProjects)
                Tiles.Add(new RecentProjectRef(path, new DirectoryInfo(path).Name));
            _ = LoadRecentsAsync();
        }
    }

    // Design-time constructor for the XAML previewer.
    public ProjectLauncherViewModel() : this(null!, null!, new ToastService(), () => { }) { }

    /// <summary>The “+ New” tile followed by one card per recent project (both rendered in the same grid).</summary>
    public ObservableCollection<ViewModelBase> Tiles { get; } = new();

    private IEnumerable<RecentProjectRef> Recents => Tiles.OfType<RecentProjectRef>();

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

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateProjectAsync()
    {
        try
        {
            var folder = Path.Combine(NewProjectLocation, NewProjectName.Trim());
            _projects.Create(folder, NewProjectName.Trim());
            await _dbFactory.EnsureCreatedAsync();
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
        var err = await TryOpenAsync(folder);
        if (err is null) IsNewProjectSheetOpen = false;
        else SheetError = "Couldn't open project: " + err;
    }

    // ── Per-project actions (card body + the ⋯ menu) ─────────────────────────

    /// <summary>Open the project behind a card (the card body and the menu's "Open" both call this).</summary>
    public async Task OpenProjectAsync(RecentProjectRef r)
    {
        var err = await TryOpenAsync(r.Path);
        if (err is not null) _toasts.Show($"Couldn't open “{r.Name}”: {err}", isError: true);
    }

    private async Task<string?> TryOpenAsync(string folder)
    {
        try
        {
            _projects.Open(folder);
            await _dbFactory.EnsureCreatedAsync();
            _onProjectOpened();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

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
        r.Thumb0 = r.Thumb1 = r.Thumb2 = null; // the collage came from those thumbnails
        _toasts.Show($"Cleared {ByteFormat.Format(freed)} of thumbnails for “{r.Name}” (they rebuild as you browse).");
    }

    /// <summary>Remove a project from the list without deleting any files.</summary>
    public void Forget(RecentProjectRef r)
    {
        _projects.RemoveFromRecents(r.Path);
        Tiles.Remove(r);
        _toasts.Show($"Removed “{r.Name}” from the list (files left untouched).");
    }

    /// <summary>Permanently delete a project's folder. Destructive — the view confirms first.</summary>
    public void DeleteFromDisk(RecentProjectRef r)
    {
        try
        {
            _projects.DeleteProject(r.Path);
            Tiles.Remove(r);
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
                var files = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.png").Take(3).ToList()
                    : new List<string>();
                return (sz, files.Select(LoadThumbnail).ToList());
            });

            // Back on the UI thread (the await resumed here): publish to the card.
            r.CacheSizeText = ByteFormat.Format(size) + " cache";
            if (thumbs.Count > 0) r.Thumb0 = thumbs[0];
            if (thumbs.Count > 1) r.Thumb1 = thumbs[1];
            if (thumbs.Count > 2) r.Thumb2 = thumbs[2];
        }
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
