namespace Hoard.Core.Domain;

/// <summary>
/// One source board merged into a local <see cref="Collection"/>. A local board can gather pins from several
/// Pinterest boards (a "merge"); each contributing board is recorded here so it can be listed, re-synced, or
/// removed. The originating board id also drives incremental re-import (the connector's skip-archive).
/// </summary>
public class CollectionSource
{
    public int Id { get; set; }

    public int CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    public string SourceConnector { get; set; } = "";

    /// <summary>Stable board id on the source side (e.g. the Pinterest board id). Null if the source exposed none.</summary>
    public string? SourceBoardId { get; set; }

    /// <summary>The board URL this source was imported from (used to open it again or re-sync).</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>The source board's own name at import time, for display in the merge list.</summary>
    public string? Name { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Cross-device identity for archive ops (schema v8) — same contract as <see cref="Collection.Uid"/>,
    /// declared last for the same DDL-parity reason.</summary>
    public string? Uid { get; set; }
}
