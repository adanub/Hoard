namespace Hoard.Core.Domain;

/// <summary>
/// A single saved piece of media (image / gif / video). The <see cref="Sha256"/> of the
/// original bytes is the dedupe key: the same file imported from two different boards is
/// stored once and linked twice.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the original file bytes. Unique.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Path of the blob inside the content-addressed store, relative to the store root.</summary>
    public string RelativePath { get; set; } = "";

    public string? MimeType { get; set; }
    public MediaKind Kind { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Bytes { get; set; }

    /// <summary>Connector that produced this asset, e.g. "pinterest".</summary>
    public string SourceConnector { get; set; } = "";

    /// <summary>Stable id on the source side, e.g. the Pinterest pin id.</summary>
    public string? SourceId { get; set; }

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
    /// When set, this asset has been curated out: its blob is deleted from disk but the row is kept as a
    /// <b>tombstone</b> (so the removal is global, recorded, and undoable). The tile shows
    /// <see cref="DeletionNote"/> instead of the missing media, and Restore re-fetches it from its source.
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
