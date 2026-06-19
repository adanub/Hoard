using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hoard.Core.Projects;

/// <summary>
/// A Hoard project: a user-chosen folder that holds all of one archive's data — the media blob
/// store, the metadata database, and import logs. Projects are self-contained and portable, so a
/// whole archive can be moved, backed up, or synced just by moving its folder.
/// </summary>
public sealed class HoardProject
{
    /// <summary>Filename of the marker that identifies a folder as a Hoard project.</summary>
    public const string MarkerFileName = "hoard.project.json";

    /// <summary>Current marker format version (distinct from the DB <c>user_version</c>). Bump if the marker
    /// JSON shape changes so an old marker can be recognised and migrated.</summary>
    public const int CurrentMarkerVersion = 1;

    public string Root { get; }
    public string Name { get; private set; }

    public string StoreRoot => Path.Combine(Root, "store");
    public string DatabasePath => Path.Combine(Root, "hoard.db");
    public string LogsRoot => Path.Combine(Root, "logs");
    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    /// <summary>Regenerable cache of small thumbnails, keyed by content hash.</summary>
    public string ThumbnailsRoot => ThumbnailsDir(Root);

    /// <summary>The thumbnails cache directory for a project at <paramref name="projectRoot"/>.</summary>
    public static string ThumbnailsDir(string projectRoot) => Path.Combine(projectRoot, "thumbnails");

    /// <summary>Connector "already-fetched" archive, so re-imports skip items already backed up here.</summary>
    public string DownloadArchivePath => Path.Combine(Root, "download-archive.db");

    private HoardProject(string root, string name)
    {
        Root = Path.GetFullPath(root);
        Name = name;
    }

    /// <summary>True if <paramref name="folder"/> already contains a Hoard project marker.</summary>
    public static bool IsProject(string folder) => File.Exists(Path.Combine(folder, MarkerFileName));

    /// <summary>
    /// True if the folder is a Hoard project OR a recognizable remnant of one (e.g. a half-deleted
    /// folder that still has the database or store). Used to allow cleanup/deletion of partial state
    /// without risking deletion of an unrelated directory.
    /// </summary>
    public static bool LooksLikeProjectFolder(string folder)
        => IsProject(folder)
           || File.Exists(Path.Combine(folder, "hoard.db"))
           || File.Exists(Path.Combine(folder, "download-archive.db"))
           || Directory.Exists(Path.Combine(folder, "store"));

    // Windows reserves these device names regardless of extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Validate a project name for use as a folder name. Returns a human-readable error, or null if
    /// the name is usable. Keeps project creation from failing with a cryptic filesystem error.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a project name.";

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Name can't contain any of: \\ / : * ? \" < > |";
        if (trimmed.EndsWith('.'))
            return "Name can't end with a period.";
        if (ReservedNames.Contains(trimmed))
            return $"\"{trimmed}\" is a name reserved by Windows. Choose another.";
        return null;
    }

    /// <summary>
    /// Create a new project in <paramref name="folder"/> (created if needed), writing the marker and
    /// data sub-directories. Throws if the folder already holds a project.
    /// </summary>
    public static HoardProject Create(string folder, string? name = null)
    {
        if (name is not null && ValidateName(name) is { } error)
            throw new ArgumentException(error, nameof(name));

        var full = Path.GetFullPath(folder);
        if (IsProject(full))
            throw new InvalidOperationException($"'{full}' is already a Hoard project. Open it instead.");

        Directory.CreateDirectory(full);
        var project = new HoardProject(full, string.IsNullOrWhiteSpace(name) ? DeriveName(full) : name.Trim());
        project.EnsureSubdirectories();
        project.WriteMarker(new ProjectMarker { Name = project.Name, SchemaVersion = CurrentMarkerVersion });
        return project;
    }

    /// <summary>
    /// Adopt a folder that holds project data (a database or store) but whose marker is missing or unreadable:
    /// (re)write a valid marker so it opens as a normal project. Preserves a still-readable marker name, else
    /// derives one from the folder. Throws if the folder shows no sign of being a project, so an unrelated
    /// directory is never stamped. Use this for the explicit "open existing folder" recovery path.
    /// </summary>
    public static HoardProject Adopt(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!LooksLikeProjectFolder(full))
            throw new InvalidOperationException($"'{full}' doesn't look like a Hoard project (no database or store).");

        string? existingName = null;
        if (File.Exists(Path.Combine(full, MarkerFileName)))
        {
            // A present-but-corrupt marker: keep its name if we can still read one, otherwise fall back.
            try { existingName = JsonSerializer.Deserialize<ProjectMarker>(File.ReadAllText(Path.Combine(full, MarkerFileName)))?.Name; }
            catch { /* unreadable marker — derive the name from the folder */ }
        }

        var project = new HoardProject(full, string.IsNullOrWhiteSpace(existingName) ? DeriveName(full) : existingName!);
        project.EnsureSubdirectories();
        project.WriteMarker(new ProjectMarker { Name = project.Name, SchemaVersion = CurrentMarkerVersion });
        return project;
    }

    /// <summary>Open an existing project folder. Throws if it isn't one.</summary>
    public static HoardProject Open(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!IsProject(full))
            throw new InvalidOperationException($"'{full}' is not a Hoard project (no {MarkerFileName}).");

        // Tolerate a present-but-malformed marker: fall back to the folder name rather than failing the open.
        ProjectMarker? marker = null;
        try { marker = JsonSerializer.Deserialize<ProjectMarker>(File.ReadAllText(Path.Combine(full, MarkerFileName))); }
        catch { /* unreadable marker — derive the name from the folder below */ }

        var project = new HoardProject(full, string.IsNullOrWhiteSpace(marker?.Name) ? DeriveName(full) : marker!.Name!);
        project.EnsureSubdirectories();
        return project;
    }

    private void EnsureSubdirectories()
    {
        Directory.CreateDirectory(StoreRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(ThumbnailsRoot);
    }

    private void WriteMarker(ProjectMarker marker)
        => File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker, MarkerJson));

    /// <summary>Update the stored project name in a folder's marker (used when renaming), preserving the
    /// schema version. Safe if the marker is missing/partial.</summary>
    public static void SetStoredName(string folder, string name)
    {
        var markerPath = Path.Combine(folder, MarkerFileName);
        var marker = File.Exists(markerPath)
            ? JsonSerializer.Deserialize<ProjectMarker>(File.ReadAllText(markerPath)) ?? new ProjectMarker()
            : new ProjectMarker();
        marker.Name = name;
        if (marker.SchemaVersion == 0) marker.SchemaVersion = 1;
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, MarkerJson));
    }

    private static string DeriveName(string folder)
    {
        var leaf = new DirectoryInfo(folder).Name;
        return string.IsNullOrWhiteSpace(leaf) ? "Hoard Project" : leaf;
    }

    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    private sealed class ProjectMarker
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    }
}
