namespace Hoard.Core.Storage;

/// <summary>
/// Moves files/folders to the OS recycle bin (or platform equivalent) instead of deleting them outright, so a
/// destructive action stays recoverable. Platform-neutral abstraction: the implementation is desktop-specific
/// (Windows shell / VisualBasic today) and lives in the host, keeping Core free of any platform dependency.
/// When no recycler is supplied, callers fall back to a permanent delete.
/// </summary>
public interface IFileRecycler
{
    /// <summary>Recycle a directory and all its contents. Throws if it can't (e.g. files still locked).</summary>
    void RecycleDirectory(string path);

    /// <summary>Recycle a single file. Throws if it can't.</summary>
    void RecycleFile(string path);
}
