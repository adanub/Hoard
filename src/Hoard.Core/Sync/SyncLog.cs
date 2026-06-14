using Hoard.Core.Domain;
using Hoard.Core.Metadata;

namespace Hoard.Core.Sync;

/// <summary>
/// Appends change records to the per-project sync log. Both the ingest (add) and curation (remove)
/// paths funnel through here, so the log is the single, complete history of what entered and left the
/// library — the input Phase 3 will replay to reconcile across devices. Callers add the op to the same
/// <see cref="HoardDbContext"/> as the change it describes, so one <c>SaveChanges</c> commits both
/// atomically.
/// </summary>
public static class SyncLog
{
    public static void RecordAdd(HoardDbContext db, Asset asset) => Append(db, SyncOpKind.Add, asset.Sha256);

    public static void RecordRemove(HoardDbContext db, string sha256) => Append(db, SyncOpKind.Remove, sha256);

    private static void Append(HoardDbContext db, SyncOpKind op, string sha256) =>
        db.SyncOps.Add(new SyncOp
        {
            Op = op,
            EntityType = SyncEntityType.Asset,
            EntityKey = sha256,
            Timestamp = DateTimeOffset.UtcNow,
        });
}
