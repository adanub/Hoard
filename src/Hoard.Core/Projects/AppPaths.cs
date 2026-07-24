namespace Hoard.Core.Projects;

/// <summary>
/// App-level (not project-level) locations: the small settings file that remembers recent projects,
/// and a global diagnostic log. Gallery data never lives here — that's per-project (see
/// <see cref="HoardProject"/>).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string appDataRoot)
    {
        AppDataRoot = appDataRoot;
        LogsRoot = Path.Combine(appDataRoot, "logs");
        SettingsFile = Path.Combine(appDataRoot, "settings.json");
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string AppDataRoot { get; }
    public string LogsRoot { get; }
    public string SettingsFile { get; }

    /// <summary>
    /// This machine's derived state for one project (format v2, <c>SYNC-DESIGN.md</c> P3): the index DB
    /// rebuilt from the archive's op segments. Keyed by the project's stable id, so moving or renaming
    /// the project folder never orphans it.
    /// </summary>
    public string ProjectStateRoot(Guid projectId) => Path.Combine(AppDataRoot, "projects", projectId.ToString("N"));

    /// <summary>The derived index database for one project — rebuildable, never the source of truth.</summary>
    public string IndexDbPath(Guid projectId) => Path.Combine(ProjectStateRoot(projectId), "index.db");

    /// <summary>Default per-user app data directory, e.g. %APPDATA%/Hoard.</summary>
    public static AppPaths Default()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoard"));
}
