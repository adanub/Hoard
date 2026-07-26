namespace Hoard.Core.Domain;

/// <summary>The kind of change recorded in the sync log.</summary>
public enum SyncOpKind
{
    Add = 1,
    Remove = 2,
}

/// <summary>The kind of entity a <see cref="SyncOp"/> targets. An enum (not a string) so it survives schema growth cheaply.</summary>
public enum SyncEntityType
{
    Asset = 1,
}

/// <summary>
/// LEGACY, retired: the pre-archive-format change log. <c>Sync/ArchiveLog</c> (the v2 per-device op
/// segments) is the replayable history now; nothing writes or reads this table any more. The entity and
/// its table are kept only for schema stability — existing project DBs have it, and dropping a table is
/// the one non-additive change the versioning scheme avoids.
/// </summary>
public class SyncOp
{
    public long Id { get; set; }

    public SyncOpKind Op { get; set; }

    public SyncEntityType EntityType { get; set; }

    /// <summary>Stable cross-device identity of the target — the asset's lowercase hex SHA-256.</summary>
    public string EntityKey { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; }
}
