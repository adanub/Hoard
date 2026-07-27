using System;
using System.IO;

namespace Hoard.Desktop.Services;

/// <summary>
/// The one rule every "choose a folder" picker shares: a backup or export destination must sit OUTSIDE
/// the project, because the archive folder is allowed to hold only the archive itself.
/// <para>For an export, the folder to test is the one the run will CREATE — <c>&lt;chosen&gt;/&lt;name&gt;</c>
/// — not the one the user picked: choosing the project's own parent folder lands the export straight back
/// on the project folder whenever the export's top-level name matches it.</para>
/// </summary>
public static class DestinationFolder
{
    /// <summary>True when <paramref name="candidate"/> is the project folder or lives inside it.</summary>
    public static bool IsInsideProject(string candidate, string projectRoot)
        => WithSeparator(Path.GetFullPath(candidate))
            .StartsWith(WithSeparator(Path.GetFullPath(projectRoot)), StringComparison.OrdinalIgnoreCase);

    // The trailing separator is load-bearing: a plain StartsWith would also reject a SIBLING whose name
    // merely extends the project's ("Hoard" vs "Hoard-backup" — a natural backup-folder name).
    private static string WithSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
