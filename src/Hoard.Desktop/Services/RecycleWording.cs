using System;

namespace Hoard.Desktop.Services;

/// <summary>
/// Platform-honest wording for the delete flows. Files reach the OS recycle bin only where an
/// <c>IFileRecycler</c> is registered (Windows — see the App DI gate); elsewhere the fallback deletes
/// permanently, and every confirm/toast must say so rather than promise a recoverability that isn't there.
/// </summary>
public static class RecycleWording
{
    /// <summary>Mirrors the DI registration gate in App — keep the two in step.</summary>
    public static bool RecyclerAvailable => OperatingSystem.IsWindows();

    /// <summary>Confirm-message clause: what happens to the files.</summary>
    public static string FilesFate =>
        RecyclerAvailable ? "files go to your recycle bin" : "files are permanently deleted";

    /// <summary>Toast tail after a count: "N image(s) …".</summary>
    public static string SentFate =>
        RecyclerAvailable ? "sent to the recycle bin" : "permanently deleted";
}
