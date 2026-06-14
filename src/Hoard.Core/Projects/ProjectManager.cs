using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly List<string> _recent = new();

    public ProjectManager(AppPaths appPaths, ILogger<ProjectManager>? logger = null)
    {
        _appPaths = appPaths;
        _logger = logger ?? NullLogger<ProjectManager>.Instance;
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

    /// <summary>Forget a project (remove from the recent list) without touching its files.</summary>
    public void RemoveFromRecents(string folder)
    {
        if (_recent.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    /// <summary>
    /// Permanently delete a project's folder and all its data, then forget it. Guarded so it only ever
    /// deletes a Hoard project (or a recognizable remnant) — never an arbitrary directory. The folder
    /// is forgotten only after a successful delete, so a failure leaves it retryable in the list.
    /// </summary>
    public void DeleteProject(string folder)
    {
        if (!HoardProject.LooksLikeProjectFolder(folder))
            throw new InvalidOperationException($"'{folder}' is not a Hoard project; refusing to delete it.");

        if (Current is not null && string.Equals(Current.Root, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            Current = null;

        DeleteDirectoryResilient(folder);   // throws if it can't (e.g. files still locked)
        RemoveFromRecents(folder);
        _logger.LogInformation("Deleted project folder {Folder}", folder);
    }

    /// <summary>
    /// Delete a directory, first releasing any pooled SQLite connections (and their WAL handles) that
    /// would otherwise lock the database files. Retries a couple of times to ride out transient locks.
    /// </summary>
    private static void DeleteDirectoryResilient(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, recursive: true);
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
