using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>
/// The whole "sync with the remote" operation (P5/R2), composed from the replicator: <b>pull</b> what
/// other machines pushed → <b>apply</b> the arrived ops to this machine's index (the same catch-up an
/// open runs, so the UI can refresh immediately instead of waiting for the next open) → <b>flush</b> this
/// device's own pending ops to its segment → <b>push</b> everything local the remote lacks. A remote with
/// no archive yet skips the pull and is seeded by the push. Safe to re-run any time; every step is
/// idempotent.
/// <para>The flush is not optional and not just tidiness: in <see cref="ReplicationMode.Delta"/> the
/// segment IS what push reads to decide which blobs the remote needs, so an op still sitting in the table
/// would leave its image out of the backup while the run cheerfully reported "already in sync".</para>
/// </summary>
public static class RemoteSync
{
    public static async Task<ReplicationReport> SyncAsync(
        HoardProject project, IRemoteStore remote,
        IDbContextFactory<HoardDbContext> dbFactory, ArchiveLog archive,
        ILogger? logger = null, ReplicationMode mode = ReplicationMode.Delta,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // ONE marker read for the whole run (it used to be three: here, and inside each leg's own check).
        // Seeding an empty remote up front is the same write the push would have done.
        var hasArchive = await ArchiveReplicator
            .EnsureSameArchiveAsync(project, remote, seedIfEmpty: true, ct).ConfigureAwait(false);

        var pull = new ReplicationReport(0, 0, 0, 0);
        if (hasArchive)
        {
            // Our device id rides along so the pull never replaces this device's own existing chapters
            // (the local writer's copy is authoritative; it only bootstraps them when absent).
            pull = await ArchiveReplicator.PullAsync(
                project, remote, archive.DeviceId, mode, progress, ct, markerVerified: true).ConfigureAwait(false);
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

        // Land any op the table holds but the segment doesn't, BEFORE push reads the segment as its
        // cursor. Cheap when there is nothing pending (one indexed query), and the difference between a
        // complete backup and a silently incomplete one when a session ended mid-import.
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
            await archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        }

        var push = await ArchiveReplicator.PushAsync(
            project, remote, archive.DeviceId, mode, progress, ct, markerVerified: true).ConfigureAwait(false);
        return new ReplicationReport(
            push.BlobsPushed, push.ChaptersPushed, pull.BlobsPulled, pull.ChaptersPulled,
            push.ChaptersDeferred + pull.ChaptersDeferred,
            push.BlobsUnavailable + pull.BlobsUnavailable,
            push.Verified && (pull.Verified || !hasArchive));
    }
}
