using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoard.Core.Projects;

/// <summary>
/// Tracks the currently open <see cref="HoardProject"/> and the list of recently used project
/// folders, persisting both to the app settings file. The DB factory and media store read
/// <see cref="Current"/> so switching projects re-points all storage at the new folder.
/// </summary>
public sealed class ProjectManager
{
    private readonly AppPaths _appPaths;
    private readonly ILogger<ProjectManager> _logger;
    private readonly IFileRecycler? _recycler;
    private readonly List<string> _recent = new();

    public ProjectManager(AppPaths appPaths, ILogger<ProjectManager>? logger = null, IFileRecycler? recycler = null)
    {
        _appPaths = appPaths;
        _logger = logger ?? NullLogger<ProjectManager>.Instance;
        _recycler = recycler;
        Load();
    }

    /// <summary>The open project, or null when none is open (first run / after the folder went missing).</summary>
    public HoardProject? Current { get; private set; }

    /// <summary>Recently opened project folders, most-recent first.</summary>
    public IReadOnlyList<string> RecentProjects => _recent;

    public HoardProject Create(string folder, string? name = null)
    {
        var project = HoardProject.Create(folder, name);
        SetCurrent(project);
        return project;
    }

    public HoardProject Open(string folder)
    {
        var project = HoardProject.Open(folder);
        SetCurrent(project);
        return project;
    }

    /// <summary>
    /// Adopt a folder that holds project data but has lost (or has a corrupt) marker: rewrite the marker and
    /// open it. For the explicit "open existing folder" recovery path — throws if the folder doesn't look like
    /// a project.
    /// </summary>
    public HoardProject Adopt(string folder)
    {
        var project = HoardProject.Adopt(folder);
        SetCurrent(project);
        return project;
    }

    /// <summary>Re-open the last-used project on startup, if its folder still exists. Returns it or null.</summary>
    public HoardProject? OpenLastOrNull()
    {
        foreach (var path in _recent.ToList())
        {
            if (HoardProject.IsProject(path))
            {
                try { return Open(path); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not open recent project {Path}", path); }
            }
            else
            {
                _recent.Remove(path); // prune folders that were moved/deleted
            }
        }
        Save();
        return null;
    }

    /// <summary>
    /// Rename a project by renaming its folder on disk to a sibling with <paramref name="newName"/>, updating
    /// the marker and the recents entry (and <see cref="Current"/> if it's the open one). Returns the new
    /// folder path. Throws on an invalid name, a name collision, or if the folder can't be moved (locked).
    /// </summary>
    public string RenameProject(string folder, string newName)
    {
        if (HoardProject.ValidateName(newName) is { } error)
            throw new ArgumentException(error, nameof(newName));

        var full = Path.GetFullPath(folder);
        if (!HoardProject.LooksLikeProjectFolder(full))
            throw new InvalidOperationException($"'{full}' is not a Hoard project; refusing to rename it.");

        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("Can't rename a project at a drive root.");
        var target = Path.Combine(parent, newName.Trim());
        var sameFolder = string.Equals(target, full, StringComparison.OrdinalIgnoreCase);
        if (!sameFolder && Directory.Exists(target))
            throw new InvalidOperationException($"A folder named '{newName.Trim()}' already exists here.");

        var wasCurrent = Current is not null &&
            string.Equals(Current.Root, full, StringComparison.OrdinalIgnoreCase);

        if (!sameFolder) MoveDirectoryResilient(full, target);
        HoardProject.SetStoredName(target, newName.Trim());

        var idx = _recent.FindIndex(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _recent[idx] = target;
        if (wasCurrent) Current = HoardProject.Open(target);
        Save();
        _logger.LogInformation("Renamed project folder {Old} → {New}", full, target);
        return target;
    }

    /// <summary>Move a directory, first releasing pooled SQLite handles that would lock it; retries briefly.</summary>
    private static void MoveDirectoryResilient(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    /// <summary>Forget a project (remove from the recent list) without touching its files.</summary>
    public void RemoveFromRecents(string folder)
    {
        if (_recent.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    /// <summary>
    /// Delete a project's folder and all its data, then forget it. When a <see cref="IFileRecycler"/> was
    /// supplied the folder is sent to the OS recycle bin (recoverable); otherwise it's deleted permanently.
    /// Guarded so it only ever removes a Hoard project (or a recognizable remnant) — never an arbitrary
    /// directory. The folder is forgotten only after a successful removal, so a failure leaves it retryable.
    /// </summary>
    public void DeleteProject(string folder)
    {
        if (!HoardProject.LooksLikeProjectFolder(folder))
            throw new InvalidOperationException($"'{folder}' is not a Hoard project; refusing to delete it.");

        if (Current is not null && string.Equals(Current.Root, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            Current = null;

        RemoveDirectoryResilient(folder);   // throws if it can't (e.g. files still locked)
        RemoveFromRecents(folder);
        _logger.LogInformation("{Action} project folder {Folder}", _recycler is null ? "Deleted" : "Recycled", folder);
    }

    /// <summary>
    /// Remove a directory — to the recycle bin via <see cref="_recycler"/> when present, else permanently —
    /// first releasing any pooled SQLite connections (and their WAL handles) that would otherwise lock the
    /// database files. Retries a couple of times to ride out transient locks.
    /// </summary>
    private void RemoveDirectoryResilient(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (_recycler is not null) _recycler.RecycleDirectory(folder);
                else Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                // A handle may still be closing; nudge finalizers and retry.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private void SetCurrent(HoardProject project)
    {
        Current = project;
        _recent.RemoveAll(p => string.Equals(p, project.Root, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, project.Root);
        if (_recent.Count > 10) _recent.RemoveRange(10, _recent.Count - 10);
        Save();
        _logger.LogInformation("Opened project '{Name}' at {Root}", project.Name, project.Root);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_appPaths.SettingsFile)) return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_appPaths.SettingsFile));
            if (settings?.RecentProjects is { } recents)
                _recent.AddRange(recents.Where(Directory.Exists));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings; starting fresh.");
        }
    }

    private void Save()
    {
        try
        {
            var settings = new AppSettings { RecentProjects = _recent.ToList() };
            File.WriteAllText(_appPaths.SettingsFile,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save settings.");
        }
    }

    private sealed class AppSettings
    {
        [JsonPropertyName("recentProjects")] public List<string> RecentProjects { get; set; } = new();
    }
}
