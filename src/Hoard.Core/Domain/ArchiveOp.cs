namespace Hoard.Core.Domain;

/// <summary>
/// One record of the replayable archive op log (<c>SYNC-DESIGN.md</c>): the full history of every change
/// to the archive, carrying enough payload that the metadata database can be rebuilt from the log alone.
/// P1 keeps the log in a table inside the project DB; P2 moves it to per-device append-only segment
/// files, at which point <see cref="DeviceId"/>/<see cref="Seq"/> name the segment and the position in
/// it. Append-only — ops are never mutated or deleted.
/// </summary>
public class ArchiveOp
{
    public int Id { get; set; }

    /// <summary>The device that wrote the op — the only device allowed to extend its (DeviceId, Seq) line.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Per-device monotonic sequence; (DeviceId, Seq) is the op's identity, so replay dedups on it.</summary>
    public long Seq { get; set; }

    /// <summary>Sortable hybrid-logical-clock timestamp (see <see cref="Sync.HybridClock"/>) — cross-device order.</summary>
    public string Hlc { get; set; } = "";

    /// <summary>Op kind, e.g. <c>asset.added</c> — the catalogue lives in <see cref="Sync.ArchiveOpKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Asset key (content hash) for asset-scoped ops; null for collection/source ops.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Collection/source uid for ops scoped to one; null for asset lifecycle ops.</summary>
    public string? EntityUid { get; set; }

    /// <summary>Kind-specific payload (JSON), carrying whatever replay needs beyond the keys.</summary>
    public string? PayloadJson { get; set; }
}
