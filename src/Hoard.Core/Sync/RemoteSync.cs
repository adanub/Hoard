using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>
/// The whole "sync with the remote" operation (P5/R2), composed from the replicator: <b>pull</b> what
/// other machines pushed → <b>apply</b> the arrived ops to this machine's index (the same catch-up an
/// open runs, so the UI can refresh immediately instead of waiting for the next open) → <b>push</b>
/// everything local the remote lacks. A remote with no archive yet skips the pull and is seeded by the
/// push. Safe to re-run any time; every step is idempotent.
/// </summary>
public static class RemoteSync
{
    public static async Task<ReplicationReport> SyncAsync(
        HoardProject project, IRemoteStore remote,
        IDbContextFactory<HoardDbContext> dbFactory, ArchiveLog archive,
        ILogger? logger = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var pull = new ReplicationReport(0, 0, 0, 0);
        var hasArchive = await remote.ReadTextAsync(Projects.HoardProject.MarkerFileName, ct).ConfigureAwait(false) is not null;
        if (hasArchive)
        {
            // Our device id rides along so the pull never replaces this device's own existing chapters
            // (the local writer's copy is authoritative; it only bootstraps them when absent).
            pull = await ArchiveReplicator.PullAsync(project, remote, archive.DeviceId, progress, ct).ConfigureAwait(false);
            if (pull.ChaptersPulled > 0)
            {
                progress?.Report("Applying changes…");
                // The pull may have (re)written segment files under the log — its cached flush watermark
                // must be re-derived from disk or the next flush could append duplicates.
                archive.InvalidateFlushWatermark();
                await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await ArchiveSync.SyncAtOpenAsync(db, project.OpsRoot, archive, logger, ct).ConfigureAwait(false);
            }
        }

        var push = await ArchiveReplicator.PushAsync(project, remote, progress, ct).ConfigureAwait(false);
        return new ReplicationReport(push.BlobsPushed, push.ChaptersPushed, pull.BlobsPulled, pull.ChaptersPulled);
    }
}
