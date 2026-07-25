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
    /// <summary>Returns null when there's no local database to read — for a v2 project that's a machine
    /// that hasn't opened (and therefore indexed) it yet, which is not the same thing as "0 images";
    /// callers should say so rather than show zeros.</summary>
    public static async Task<ProjectStats?> ReadAsync(string projectFolder, AppPaths appPaths, CancellationToken ct = default)
    {
        var dbPath = appPaths.LocalDatabasePath(projectFolder);
        if (!File.Exists(dbPath)) return null;

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
