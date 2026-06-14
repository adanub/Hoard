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
/// An append-only record of a library change. Keyed by the <b>content identity</b> (the asset's
/// SHA-256) rather than the local row id, so an op can be replayed on another device that holds the
/// same content under a different local id. This is the foundation for Phase 3 cloud reconciliation —
/// nothing consumes it yet, but every add (on ingest) and remove (on curation) is logged from the start
/// so no history is lost before sync ships.
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
