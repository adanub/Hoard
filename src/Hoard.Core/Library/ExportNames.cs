namespace Hoard.Core.Library;

/// <summary>
/// Pure naming rules for the human-readable export (<see cref="BoardExporter"/>): turn board/folder
/// titles and asset metadata into portable file-system names. Applies the Windows-invalid rule set on
/// every OS (like <see cref="Projects.HoardProject.ValidateName"/>) so an export made on one platform
/// stays browsable on the others.
/// </summary>
public static class ExportNames
{
    // The Windows-invalid set, applied on every OS — an exported tree must stay portable.
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    // Windows reserves these device names regardless of extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Reduce a free-text title to a safe path component: invalid/control characters become spaces,
    /// whitespace collapses, trailing dots go (Windows strips them silently), length is capped at a
    /// word boundary where possible. Returns "" when nothing printable survives.
    /// </summary>
    public static string SanitiseComponent(string? name, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var chars = name.Select(c => c < ' ' || InvalidNameChars.Contains(c) ? ' ' : c).ToArray();
        var collapsed = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > maxLength)
        {
            var cut = collapsed.LastIndexOf(' ', maxLength - 1);
            collapsed = collapsed[..(cut > maxLength / 2 ? cut : maxLength)];
        }
        collapsed = collapsed.TrimEnd('.', ' ');
        if (ReservedNames.Contains(collapsed)) collapsed += "_";
        return collapsed;
    }

    /// <summary>
    /// The exported file name for one asset. Title-based names always carry the pin id (or a content-hash
    /// stub) in brackets so every name is unique and stable per asset — a re-export lands on the same
    /// names, which is what makes the up-to-date skip work.
    /// </summary>
    public static string FileName(string? title, string? sourceId, string sha256, string? extension)
    {
        var key = SanitiseComponent(sourceId, 40);
        if (key.Length == 0) key = sha256[..Math.Min(12, sha256.Length)];

        var stem = SanitiseComponent(title, 60);
        var name = stem.Length == 0 ? key : $"{stem} [{key}]";

        var ext = extension?.TrimStart('.');
        return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
    }

    /// <summary>The exported directory name for a board/folder; the id backstops an unprintable title.</summary>
    public static string FolderName(string? name, int collectionId)
    {
        var component = SanitiseComponent(name);
        return component.Length == 0 ? $"Untitled [{collectionId}]" : component;
    }

    /// <summary>The exported directory name for a whole project — the folder its boards land in. A project
    /// has no id to fall back on (its identity is a GUID nobody wants in a path), so an unprintable name
    /// gets a plain generic one.</summary>
    public static string ProjectFolderName(string? name)
    {
        var component = SanitiseComponent(name);
        return component.Length == 0 ? "Hoard project" : component;
    }
}
