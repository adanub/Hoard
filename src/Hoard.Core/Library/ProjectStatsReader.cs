using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

/// <summary>Live (non-deleted) asset counts by kind + top-level board count for one project.</summary>
public sealed record ProjectStats(int Images, int Gifs, int Videos, int Boards);

/// <summary>
/// Reads at-a-glance stats straight from a project's <c>hoard.db</c> by <b>path</b> — without going through
/// <see cref="ProjectManager.Current"/> — so the launcher's Edit popup can show counts for a project that
/// isn't the open one. Pooling=False leaves no lingering file lock on the project folder.
/// </summary>
public static class ProjectStatsReader
{
    public static async Task<ProjectStats> ReadAsync(string projectFolder, AppPaths appPaths, CancellationToken ct = default)
    {
        // Format v2 keeps the metadata in this machine's derived index; legacy v1 in the folder itself.
        // Peek (side-effect-free) rather than Open — this runs for every recents card.
        var (format, id) = HoardProject.Peek(projectFolder);
        var dbPath = format >= HoardProject.CurrentFormatVersion && id != default
            ? appPaths.IndexDbPath(id)
            : Path.Combine(projectFolder, "hoard.db");
        if (!File.Exists(dbPath)) return new ProjectStats(0, 0, 0, 0);

        await using var db = ProjectDbContextFactory.CreateForPath(dbPath);

        // Count live assets per kind (tombstones — DeletedAt set — don't count); boards = top-level collections.
        var live = db.Assets.Where(a => a.DeletedAt == null);
        var images = await live.CountAsync(a => a.Kind == MediaKind.Image, ct);
        var gifs = await live.CountAsync(a => a.Kind == MediaKind.Gif, ct);
        var videos = await live.CountAsync(a => a.Kind == MediaKind.Video, ct);
        var boards = await db.Collections.CountAsync(c => c.ParentId == null, ct);
        return new ProjectStats(images, gifs, videos, boards);
    }
}
