using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Projects;

namespace Hoard.Desktop.ViewModels;

/// <summary>A recent project folder, shown in the launcher list (with its live thumbnail-cache size).</summary>
public partial class RecentProjectRef : ViewModelBase
{
    public string Path { get; }
    public string Name { get; }

    [ObservableProperty] private string _cacheSizeText = "Cache: …";

    public RecentProjectRef(string path, string name)
    {
        Path = path;
        Name = name;
    }
}

/// <summary>
/// The start screen: pick a recent project, open an existing project folder, or create a new
/// project by name + location (the folder is created for you). Raises a callback once a project
/// is open so the shell can switch to the library view.
/// </summary>
public partial class ProjectLauncherViewModel : ViewModelBase
{
    private readonly ProjectManager _projects;
    private readonly ProjectDbContextFactory _dbFactory;
    private readonly Action _onProjectOpened;

    public ProjectLauncherViewModel(
        ProjectManager projects, ProjectDbContextFactory dbFactory, Action onProjectOpened)
    {
        _projects = projects;
        _dbFactory = dbFactory;
        _onProjectOpened = onProjectOpened;

        if (projects is not null) // skip in the design-time (null) ctor
        {
            foreach (var path in projects.RecentProjects)
                RecentProjects.Add(new RecentProjectRef(path, new DirectoryInfo(path).Name));
            _ = LoadCacheSizesAsync();
        }
    }

    // Design-time constructor for the XAML previewer.
    public ProjectLauncherViewModel() : this(null!, null!, () => { }) { }

    public ObservableCollection<RecentProjectRef> RecentProjects { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForgetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCacheCommand))]
    private RecentProjectRef? _selectedRecent;

    [ObservableProperty] private string _newProjectName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    private string _newProjectLocation = "";

    [ObservableProperty] private string _statusText = "";

    public bool HasRecents => RecentProjects.Count > 0;

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

    [RelayCommand(CanExecute = nameof(CanOpenRecent))]
    private Task OpenRecentAsync() => OpenAsync(SelectedRecent!.Path);

    private bool CanOpenRecent() => SelectedRecent is not null;

    /// <summary>Clear the selected project's thumbnail cache (regenerated on demand, so this is safe).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRecent))]
    private async Task ClearCacheAsync()
    {
        var target = SelectedRecent;
        if (target is null) return;
        var dir = HoardProject.ThumbnailsDir(target.Path);
        var freed = await Task.Run(() =>
        {
            var size = DirectorySize(dir);
            ClearDirectory(dir);
            return size;
        });
        target.CacheSizeText = "Cache: " + ByteFormat.Format(0);
        StatusText = $"Cleared {ByteFormat.Format(freed)} of thumbnails for “{target.Name}” " +
                     "(they rebuild automatically as you browse).";
    }

    private async Task LoadCacheSizesAsync()
    {
        foreach (var item in RecentProjects.ToArray())
        {
            var dir = HoardProject.ThumbnailsDir(item.Path);
            var size = await Task.Run(() => DirectorySize(dir));
            item.CacheSizeText = "Cache: " + ByteFormat.Format(size);
        }
    }

    private static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* skip files that vanish mid-scan */ }
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

    /// <summary>Remove the selected project from the list without deleting any files.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenRecent))]
    private void Forget()
    {
        var target = SelectedRecent;
        if (target is null) return;
        _projects.RemoveFromRecents(target.Path);
        RecentProjects.Remove(target);
        OnPropertyChanged(nameof(HasRecents));
        StatusText = $"Removed “{target.Name}” from the list (files left untouched).";
    }

    /// <summary>
    /// Permanently delete the selected project's folder. Destructive — the view must confirm first.
    /// </summary>
    public void DeleteSelectedFromDisk()
    {
        var target = SelectedRecent;
        if (target is null) return;
        try
        {
            _projects.DeleteProject(target.Path);
            RecentProjects.Remove(target);
            OnPropertyChanged(nameof(HasRecents));
            StatusText = $"Deleted “{target.Name}” and all its data.";
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't delete project: " + ex.Message;
        }
    }

    /// <summary>Open an existing project folder chosen via the view's folder picker.</summary>
    public Task OpenExistingAsync(string folder) => OpenAsync(folder);

    private async Task OpenAsync(string folder)
    {
        try
        {
            _projects.Open(folder);
            await _dbFactory.EnsureCreatedAsync();
            _onProjectOpened();
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't open project: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateProjectAsync()
    {
        try
        {
            var folder = Path.Combine(NewProjectLocation, NewProjectName.Trim());
            _projects.Create(folder, NewProjectName.Trim());
            await _dbFactory.EnsureCreatedAsync();
            _onProjectOpened();
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't create project: " + ex.Message;
        }
    }

    private bool CanCreate() =>
        !string.IsNullOrWhiteSpace(NewProjectLocation) && HoardProject.ValidateName(NewProjectName) is null;
}
