namespace Hoard.Core.Domain;

/// <summary>
/// One <b>saved pin</b> (image / gif / video): the unit of identity is the source item —
/// (<see cref="SourceConnector"/>, <see cref="SourceId"/>) — not the content hash. Two different pins
/// holding identical bytes are two rows; the content-addressed store still keeps one blob on disk
/// (<see cref="Sha256"/>/<see cref="RelativePath"/> are the pointer to it, and several rows may share
/// it — never free a blob without checking for other live referrers). A pinless row (a sidecar that
/// couldn't be parsed) falls back to content identity.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file bytes — the blob pointer + integrity value, NOT the
    /// row's identity: rows may share it (same content saved as two pins), and a re-download may change
    /// it in place (the pin's content moved on).</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Path of the blob inside the content-addressed store, relative to the store root.</summary>
    public string RelativePath { get; set; } = "";

    public string? MimeType { get; set; }
    public MediaKind Kind { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Bytes { get; set; }

    /// <summary>Connector that produced this asset, e.g. "pinterest". Half of the pin identity.</summary>
    public string SourceConnector { get; set; } = "";

    /// <summary>Stable id on the source side, e.g. the Pinterest pin id — the other half of the pin
    /// identity. Null only when the sidecar couldn't be parsed (such rows key on content instead).</summary>
    public string? SourceId { get; set; }

    /// <summary>The source board this pin was saved from (v9) — first-class provenance, so orphan
    /// recovery and the skip-archive never re-parse the stored sidecar. Backfilled from
    /// <see cref="MetadataJson"/> for old rows; a null self-heals on the next re-crawl of the pin.</summary>
    public string? SourceBoardId { get; set; }

    /// <summary>The source board <i>section</i> the pin sat in, when any (v9) — so a recovered orphan
    /// re-files into its section folder rather than the board root.</summary>
    public string? SourceSectionId { get; set; }

    /// <summary>Canonical page URL on the source (the pin page).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The off-site original the pin points at, when present.</summary>
    public string? OriginalUrl { get; set; }

    public string? Title { get; set; }
    public string? Description { get; set; }

    /// <summary>Full raw connector metadata (gallery-dl sidecar) kept verbatim for future use.</summary>
    public string? MetadataJson { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>
    /// When set, this asset has been curated out: its blob is freed from disk (when no other live row
    /// shares it) but the row is kept as a <b>tombstone</b> (so the removal of THIS pin is recorded and
    /// undoable, and a re-sync won't re-fetch it). The tile shows <see cref="DeletionNote"/> instead of
    /// the missing media, and Restore re-fetches it from its source.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Why this asset was deleted — required when tombstoning, shown on the tile.</summary>
    public string? DeletionNote { get; set; }

    /// <summary>True when this asset is a tombstone (deleted, blob gone, restorable).</summary>
    public bool IsDeleted => DeletedAt is not null;

    public List<CollectionItem> CollectionItems { get; } = new();
    public List<AssetTag> AssetTags { get; } = new();
}

public enum MediaKind
{
    Unknown = 0,
    Image = 1,
    Gif = 2,
    Video = 3,
}
