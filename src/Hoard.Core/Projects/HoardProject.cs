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

    /// <summary>
    /// Current ARCHIVE format version (the <c>format</c> marker field; distinct from the marker shape
    /// version above). 1 = legacy: the metadata DB lives in the project folder. 2 = immutable archive
    /// (<c>SYNC-DESIGN.md</c> P3): the folder holds only static content (blobs + op segments + marker) and
    /// each machine derives its own index DB under app data, keyed by <see cref="Id"/>.
    /// </summary>
    public const int CurrentFormatVersion = 2;

    public string Root { get; }
    public string Name { get; private set; }

    /// <summary>
    /// Stable identity of the archive itself, minted at creation and carried in the marker. Unlike
    /// <see cref="Root"/> it survives moves/renames and means the same thing on every machine — it's what
    /// per-machine derived state (the future app-data index, see <c>SYNC-DESIGN.md</c>) is keyed by.
    /// Legacy markers without one are backfilled on open.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>The archive format this folder is in — see <see cref="CurrentFormatVersion"/>. A marker
    /// without the field reads as 1 (legacy). Upgraded by <c>Sync/ArchiveMigration</c>, never silently.</summary>
    public int FormatVersion { get; private set; } = 1;

    public string StoreRoot => StoreDir(Root);

    /// <summary>The content-addressed store directory for a project at <paramref name="projectRoot"/>.</summary>
    public static string StoreDir(string projectRoot) => Path.Combine(projectRoot, "store");
    public string DatabasePath => Path.Combine(Root, "hoard.db");
    public string LogsRoot => Path.Combine(Root, "logs");
    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    /// <summary>Regenerable cache of small thumbnails, keyed by content hash.</summary>
    public string ThumbnailsRoot => ThumbnailsDir(Root);

    /// <summary>The thumbnails cache directory for a project at <paramref name="projectRoot"/>.</summary>
    public static string ThumbnailsDir(string projectRoot) => Path.Combine(projectRoot, "thumbnails");

    /// <summary>Connector "already-fetched" archive, so re-imports skip items already backed up here.</summary>
    public string DownloadArchivePath => Path.Combine(Root, "download-archive.db");

    /// <summary>Per-device append-only op segments (<c>SYNC-DESIGN.md</c> P2): <c>ops/&lt;deviceId&gt;.jsonl</c>.</summary>
    public string OpsRoot => Path.Combine(Root, Sync.ArchiveSegments.DirectoryName);

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
           || Directory.Exists(StoreDir(folder));

    // Windows reserves these device names regardless of extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // The Windows-invalid set, applied on every OS: a project folder must stay portable across
    // platforms, and Path.GetInvalidFileNameChars() only covers the one we're running on.
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Validate a project name for use as a folder name. Returns a human-readable error, or null if
    /// the name is usable. Keeps project creation from failing with a cryptic filesystem error.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a project name.";

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(InvalidNameChars) >= 0 || trimmed.Any(char.IsControl))
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
        var project = new HoardProject(full, string.IsNullOrWhiteSpace(name) ? DeriveName(full) : name.Trim())
        {
            Id = Guid.NewGuid(),
            FormatVersion = CurrentFormatVersion, // new projects are born in the immutable-archive format
        };
        project.EnsureSubdirectories();
        project.WriteMarker();
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
        Guid existingId = default;
        var existingFormat = 0;
        if (File.Exists(Path.Combine(full, MarkerFileName)))
        {
            // A present-but-corrupt marker: keep its name/id/format if we can still read them, else fall back.
            try
            {
                var existing = JsonSerializer.Deserialize<ProjectMarker>(File.ReadAllText(Path.Combine(full, MarkerFileName)));
                existingName = existing?.Name;
                Guid.TryParse(existing?.Id, out existingId);
                existingFormat = existing?.Format ?? 0;
            }
            catch { /* unreadable marker — derive the name from the folder */ }
        }

        var project = new HoardProject(full, string.IsNullOrWhiteSpace(existingName) ? DeriveName(full) : existingName!)
        {
            Id = existingId == default ? Guid.NewGuid() : existingId,
            // A readable marker keeps its format; otherwise infer from the folder's contents.
            FormatVersion = existingFormat > 0 ? existingFormat : InferFormatFromFolder(full),
        };
        project.EnsureSubdirectories();
        project.WriteMarker();
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

        var project = new HoardProject(full, string.IsNullOrWhiteSpace(marker?.Name) ? DeriveName(full) : marker!.Name!)
        {
            // An absent/unreadable format must be INFERRED, never defaulted to legacy: a v2 project whose
            // marker was torn by a crashed write would otherwise be persisted back to v1 — re-pointing
            // SQLite at a fresh empty hoard.db inside the (possibly network-shared) folder, the exact
            // failure v2 exists to remove.
            FormatVersion = marker?.Format is > 0 and int f ? f : InferFormatFromFolder(full),
        };
        // The forward gate: an archive stamped by a NEWER build must be refused, not half-understood —
        // opening it would emit/replay ops under semantics this build doesn't know and silently diverge
        // the fleet. (Builds ≤ the pin-identity change shipped without this check — the reason the whole
        // fleet must be upgraded together across that transition; from here on, a format bump protects.)
        if (project.FormatVersion > CurrentFormatVersion)
            throw new InvalidOperationException(
                $"'{full}' was written by a newer version of Hoard (archive format {project.FormatVersion}; " +
                $"this build understands up to {CurrentFormatVersion}). Update Hoard on this machine to open it.");
        if (Guid.TryParse(marker?.Id, out var id) && id != default)
        {
            project.Id = id;
        }
        else
        {
            // Legacy (or unreadable) marker with no id: mint one and persist it so every machine that
            // opens this archive from now on agrees on its identity. Best-effort — a read-only mount
            // still opens, with a session-scoped id until a writable open lands one.
            project.Id = Guid.NewGuid();
            // The rewrite serialises the project's own state, so the (inferred, never-downgraded)
            // FormatVersion carries through — a v2 marker can't be persisted back to v1 here.
            try { project.WriteMarker(); }
            catch { /* tolerated: the open itself must not fail */ }
        }
        project.EnsureSubdirectories();
        return project;
    }

    /// <summary>
    /// Read a folder's format version and project id from its marker with <b>no side effects</b> (no
    /// directory creation, no marker rewrite) — for the launcher's stats/offer peeks. Unreadable or
    /// absent values read as (1, empty).
    /// </summary>
    public static (int FormatVersion, Guid Id) Peek(string folder)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ProjectMarker>(
                File.ReadAllText(Path.Combine(folder, MarkerFileName)));
            Guid.TryParse(marker?.Id, out var id);
            return (Math.Max(1, marker?.Format ?? 0), id);
        }
        catch
        {
            return (1, default);
        }
    }

    /// <summary>
    /// Infer a folder's archive format when its marker can't say (unreadable, or the pre-format legacy
    /// shape): a migration backup proves v2 (a stray <c>hoard.db</c> beside it is an old build's empty
    /// shell, not the archive); a <c>hoard.db</c> alone means legacy v1; anything else (store/ops-only
    /// v2 remnant, or an empty folder) reads as the current format, whose index derives from the ops.
    /// </summary>
    private static int InferFormatFromFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, "hoard.db" + Sync.ArchiveMigration.BackupSuffix))) return CurrentFormatVersion;
        if (File.Exists(Path.Combine(folder, "hoard.db"))) return 1;
        return CurrentFormatVersion;
    }

    /// <summary>Stamp the archive format (the migration's final step), preserving the rest of the marker.</summary>
    public void StampFormatVersion(int version)
    {
        FormatVersion = version;
        WriteMarker();
    }

    private void EnsureSubdirectories()
    {
        Directory.CreateDirectory(StoreRoot);
        // A v2 archive folder holds static content only — derived caches (thumbnails, logs, the
        // download archive) live in per-machine app data (P4); legacy v1 keeps its in-folder layout.
        if (FormatVersion < CurrentFormatVersion)
        {
            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(ThumbnailsRoot);
        }
    }

    /// <summary>
    /// The ONE marker writer: serialises the project's current state, so every rewrite site agrees on
    /// the field list (hand-copied per-site initialisers drifted). Anything the marker should carry must
    /// be project state first.
    /// </summary>
    private void WriteMarker()
        => File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new ProjectMarker
        {
            Name = Name,
            Id = Id.ToString("N"),
            SchemaVersion = CurrentMarkerVersion,
            Format = FormatVersion,
        }, MarkerJson));

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
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("format")] public int Format { get; set; } // 0/absent = legacy v1
    }
}
