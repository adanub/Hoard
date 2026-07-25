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

    /// <summary>This machine's thumbnail cache for one project (P4: derived data lives beside the index,
    /// not in the archive folder). Regenerable; removed with the rest of the project state.</summary>
    public string ProjectThumbnailsRoot(Guid projectId) => Path.Combine(ProjectStateRoot(projectId), "thumbnails");

    /// <summary>Per-import transcripts for one project — diagnostics, machine-local.</summary>
    public string ProjectLogsRoot(Guid projectId) => Path.Combine(ProjectStateRoot(projectId), "logs");

    /// <summary>The connector's "already fetched" skip-archive for one project. Rebuilt from the index
    /// before every import, so it's pure derived state — never part of the archive folder.</summary>
    public string ProjectDownloadArchivePath(Guid projectId) => Path.Combine(ProjectStateRoot(projectId), "download-archive.db");

    /// <summary>This machine's remote configuration for one project (P5/R2) — where its archive
    /// replicates to/from. Per-machine by design: a remote is a machine's relationship to the archive.</summary>
    public string ProjectRemoteConfigPath(Guid projectId) => Path.Combine(ProjectStateRoot(projectId), "remote.json");

    // ── Resolution for a project that ISN'T open (launcher cards, verify) ────
    // The one v1/v2 rule, via a side-effect-free marker peek: v2 with a readable id → this machine's
    // app-data state; legacy v1 — or an unreadable marker, which leaves no id to key app data by — →
    // the in-folder layout. ProjectManager's *For methods are the open-project equivalents (their
    // HoardProject always carries a minted id, so they branch on format alone).

    /// <summary>Where this machine's database for the project at <paramref name="projectFolder"/> lives.</summary>
    public string LocalDatabasePath(string projectFolder)
    {
        var (format, id) = HoardProject.Peek(projectFolder);
        return format >= HoardProject.CurrentFormatVersion && id != default
            ? IndexDbPath(id)
            : Path.Combine(projectFolder, "hoard.db");
    }

    /// <summary>This machine's thumbnail cache for the project at <paramref name="projectFolder"/>.</summary>
    public string LocalThumbnailsDir(string projectFolder)
    {
        var (format, id) = HoardProject.Peek(projectFolder);
        return format >= HoardProject.CurrentFormatVersion && id != default
            ? ProjectThumbnailsRoot(id)
            : HoardProject.ThumbnailsDir(projectFolder);
    }

    /// <summary>Default per-user app data directory, e.g. %APPDATA%/Hoard.</summary>
    public static AppPaths Default()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hoard"));
}
