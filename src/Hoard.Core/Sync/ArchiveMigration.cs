using Hoard.Core.Metadata;
using Hoard.Core.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Sync;

/// <summary>
/// One-way migration of a legacy (format v1) project to the immutable-archive format
/// (<c>SYNC-DESIGN.md</c> P3). Steps, non-destructive until the last two:
///  1. upgrade the folder's <c>hoard.db</c> in place to the current schema (≤ v8),
///  2. synthesise the op log for everything the ops don't already cover (coverage-aware, idempotent),
///  3. write this device's full segment to <c>ops/</c>,
///  4. copy the now op-complete database to this machine's app-data index,
///  5. stamp the marker v2, then rename <c>hoard.db → hoard.db.pre-v2.bak</c> (user-deletable rollback).
/// A failure anywhere before the stamp leaves the project opening exactly as before; the stamp is the
/// single point of switch, deliberately BEFORE the rename — a crash between the two leaves a fully
/// working v2 project with a lingering <c>hoard.db</c> that <see cref="TidyMigratedFolder"/> renames on a
/// later open (the reverse order would leave a v1 marker with no database: an empty-looking project and
/// no re-offer, since the offer keys on hoard.db's presence). Must run on a machine that can open the
/// legacy DB (a NAS-hosted one migrates from the Windows side — the WAL-over-SMB limit is the very thing
/// this migration removes).
/// </summary>
public static class ArchiveMigration
{
    public const string BackupSuffix = ".pre-v2.bak";

    /// <summary>True when the project is still format v1 with a legacy database to migrate.</summary>
    public static bool IsRequired(HoardProject project) =>
        project.FormatVersion < HoardProject.CurrentFormatVersion && File.Exists(project.DatabasePath);

    public static async Task MigrateAsync(HoardProject project, AppPaths appPaths, ArchiveLog archive, CancellationToken ct = default)
    {
        if (!IsRequired(project)) return;

        // 1–3: upgrade, synthesise, and write the segment — all against the legacy database.
        await using (var db = ProjectDbContextFactory.CreateForPath(project.DatabasePath))
        {
            await SchemaInitializer.InitializeAsync(db, ct).ConfigureAwait(false);
            await archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
            await ArchiveOpSynthesiser.SynthesiseAsync(db, archive.DeviceId, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // The device's complete history goes to its segment explicitly (not FlushSegmentAsync, whose
            // ops root follows the *open* project — during migration that may not be this one yet).
            var segmentPath = ArchiveSegments.SegmentPath(project.OpsRoot, archive.DeviceId);
            var written = ArchiveSegments.LastSeq(segmentPath, archive.DeviceId);
            var pending = await db.ArchiveOps
                .Where(o => o.DeviceId == archive.DeviceId && o.Seq > written)
                .OrderBy(o => o.Seq)
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);
            if (pending.Count > 0) ArchiveSegments.Append(project.OpsRoot, archive.DeviceId, pending);
        }

        // 4: the migrated DB — fully upgraded and op-complete — IS this machine's first index. A straight
        // copy is cheaper than a rebuild and provably equivalent. Release any lingering handles first so
        // the WAL is checkpointed into the main file before copying.
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(appPaths.ProjectStateRoot(project.Id));
        File.Copy(project.DatabasePath, appPaths.IndexDbPath(project.Id), overwrite: true);

        // 5: the point of switch (stamp first — see the class doc for why this ordering). The backup is
        // the user's manual rollback; the archive folder now holds only static content (marker + store +
        // ops [+ caches until P4 relocates them]).
        project.StampFormatVersion(HoardProject.CurrentFormatVersion);
        File.Move(project.DatabasePath, project.DatabasePath + BackupSuffix, overwrite: true);
    }

    /// <summary>
    /// Best-effort repair for a v2 project whose legacy DB never got renamed — a crash between the
    /// migration's stamp and rename, or an old (pre-format) build having created a stray empty
    /// <c>hoard.db</c> in the folder. Never touches an existing backup and never throws.
    /// </summary>
    public static void TidyMigratedFolder(HoardProject project)
    {
        if (project.FormatVersion < HoardProject.CurrentFormatVersion) return;
        try
        {
            if (File.Exists(project.DatabasePath) && !File.Exists(project.DatabasePath + BackupSuffix))
                File.Move(project.DatabasePath, project.DatabasePath + BackupSuffix);
        }
        catch { /* the stray file is inert either way — v2 never reads it */ }
    }
}
