using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Domain;

namespace Hoard.Core.Sync;

/// <summary>
/// The archive op catalogue (<c>SYNC-DESIGN.md</c>). Since the pin-identity change, an asset op's TRUE
/// key is the pin — (<c>Connector</c>, <c>SourceId</c>) in its payload, a deterministic natural key every
/// device derives identically (no uid minting → no cross-device aliasing). The <see cref="ArchiveOp"/>
/// <c>Sha256</c> column stays populated with the asset's content sha at emission: it is the LEGACY key
/// (pre-v9 ops carry no payload identity and replay resolves them by sha — valid because the dedup era
/// guaranteed one row per sha), the fallback key for pinless assets, and a useful blob hint. Collection/
/// source ops key on their minted uid; item ops carry both. Writers emit <i>granular</i> ops — a subtree
/// delete is expanded into explicit per-asset/per-collection ops — so replay never re-derives business
/// decisions.
/// </summary>
public static class ArchiveOpKinds
{
    public const string AssetAdded = "asset.added";
    public const string AssetTombstoned = "asset.tombstoned";
    public const string AssetRestored = "asset.restored";
    public const string AssetRefetched = "asset.refetched";
    public const string AssetRetagged = "asset.retagged";
    public const string AssetRemoved = "asset.removed";
    public const string SourceUpdated = "source.updated";
    public const string CollectionCreated = "collection.created";
    public const string CollectionRenamed = "collection.renamed";
    public const string CollectionDeleted = "collection.deleted";
    public const string SourceAttached = "source.attached";
    public const string SourceRemoved = "source.removed";
    public const string ItemLinked = "item.linked";
    public const string ItemUnlinked = "item.unlinked";
}

/// <summary>Full asset metadata — everything needed to recreate the row (the blob is already in the store,
/// which an <c>asset.added</c> op implies). Replay is an <b>upsert by pin</b>: a re-emitted added op is how
/// a refreshed pin (new bytes / new metadata / moved board) propagates, LWW by HLC order. Tags are the full
/// resulting set. Board/section provenance is explicit on post-v9 ops; a legacy op's replay derives it from
/// <c>MetadataJson</c> via the one shared sidecar parser.</summary>
public sealed record AssetAddedPayload(
    string RelativePath, string? MimeType, MediaKind Kind, int? Width, int? Height, long Bytes,
    string SourceConnector, string? SourceId, string? SourceUrl, string? OriginalUrl,
    string? Title, string? Description, string? MetadataJson,
    DateTimeOffset? CreatedAt, DateTimeOffset ImportedAt, IReadOnlyList<string>? Tags,
    string? SourceBoardId = null, string? SourceSectionId = null);

public sealed record AssetTombstonedPayload(
    string Note, DateTimeOffset DeletedAt, string? Connector = null, string? SourceId = null);

/// <summary>Restore/refetch re-download the media, which can yield different bytes — the op is keyed by
/// the OLD sha (plus the pin, post-v9) and carries the new content pointer so replay follows the same
/// transition.</summary>
public sealed record AssetContentChangedPayload(
    string Sha256, string RelativePath, long Bytes, string? Connector = null, string? SourceId = null);

/// <summary>Pin identity for ops that otherwise need no payload (<c>asset.removed</c>,
/// <c>item.unlinked</c>). Legacy ops have no payload at all — replay falls back to the sha key.</summary>
public sealed record AssetKeyPayload(string? Connector = null, string? SourceId = null);

public sealed record CollectionCreatedPayload(
    string Name, string? ParentUid, string SourceConnector, string? SourceBoardId, string? SourceUrl,
    string? SourceSectionId, DateTimeOffset CreatedAt);

public sealed record CollectionRenamedPayload(string DisplayName);

public sealed record SourceAttachedPayload(
    string CollectionUid, string SourceConnector, string? SourceBoardId, string SourceUrl, string? Name,
    DateTimeOffset AddedAt);

/// <summary>The asset's FULL resulting tag set (replacement, LWW) — emitted when a re-import attaches
/// tags to an already-held asset, whose <c>asset.added</c> op predates them.</summary>
public sealed record AssetRetaggedPayload(IReadOnlyList<string> Tags, string? Connector = null, string? SourceId = null);

/// <summary>A field update on an existing source — today only the URL backfill (a v3-era source without
/// one becomes syncable once a real import supplies it).</summary>
public sealed record SourceUpdatedPayload(string SourceUrl);

/// <summary>Keyed by (asset key, collection uid column); replay upserts, so a re-link that clears the
/// per-source attribution (a user move) is the same op with a null <see cref="SourceUid"/>.</summary>
public sealed record ItemLinkedPayload(
    string? SourceUid, string? Note, DateTimeOffset AddedAt, string? Connector = null, string? SourceId = null);

/// <summary>Deterministic asset keys shared by ingest identity, op synthesis coverage, and tests:
/// the pin when present, else the content sha (the pinless/legacy fallback).</summary>
public static class ArchiveOpKeys
{
    public static string ForAsset(string? connector, string? sourceId, string? sha) =>
        sourceId is not null ? $"pin:{connector}:{sourceId}" : $"sha:{sha}";
}

public static class ArchiveOpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(json ?? throw new InvalidDataException("Archive op payload missing."), Options)
        ?? throw new InvalidDataException("Archive op payload unreadable.");
}
