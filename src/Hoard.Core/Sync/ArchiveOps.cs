using System.Text.Json;
using System.Text.Json.Serialization;
using Hoard.Core.Domain;

namespace Hoard.Core.Sync;

/// <summary>
/// The archive op catalogue (<c>SYNC-DESIGN.md</c>). Keys live in <see cref="ArchiveOp"/> columns
/// (asset ops key on content SHA-256; collection/source ops on their minted uid; item ops on both);
/// everything else replay needs rides in the kind's payload. Writers emit <i>granular</i> ops — a
/// subtree delete is expanded into explicit per-asset/per-collection ops — so replay never re-derives
/// business decisions.
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
/// which an <c>asset.added</c> op implies). Tags ride along because they're attached at import; per-tag
/// ops can come later if a connector ever surfaces mutable tags.</summary>
public sealed record AssetAddedPayload(
    string RelativePath, string? MimeType, MediaKind Kind, int? Width, int? Height, long Bytes,
    string SourceConnector, string? SourceId, string? SourceUrl, string? OriginalUrl,
    string? Title, string? Description, string? MetadataJson,
    DateTimeOffset? CreatedAt, DateTimeOffset ImportedAt, IReadOnlyList<string>? Tags);

public sealed record AssetTombstonedPayload(string Note, DateTimeOffset DeletedAt);

/// <summary>Restore/refetch re-download the media, which can yield different bytes — the op is keyed by
/// the OLD sha and carries the new identity so replay follows the same transition.</summary>
public sealed record AssetContentChangedPayload(string Sha256, string RelativePath, long Bytes);

public sealed record CollectionCreatedPayload(
    string Name, string? ParentUid, string SourceConnector, string? SourceBoardId, string? SourceUrl,
    string? SourceSectionId, DateTimeOffset CreatedAt);

public sealed record CollectionRenamedPayload(string DisplayName);

public sealed record SourceAttachedPayload(
    string CollectionUid, string SourceConnector, string? SourceBoardId, string SourceUrl, string? Name,
    DateTimeOffset AddedAt);

/// <summary>The asset's FULL resulting tag set (replacement, LWW) — emitted when a re-import attaches
/// tags to an already-held asset, whose <c>asset.added</c> op predates them.</summary>
public sealed record AssetRetaggedPayload(IReadOnlyList<string> Tags);

/// <summary>A field update on an existing source — today only the URL backfill (a v3-era source without
/// one becomes syncable once a real import supplies it).</summary>
public sealed record SourceUpdatedPayload(string SourceUrl);

/// <summary>Keyed by (sha column, collection uid column); replay upserts, so a re-link that clears the
/// per-source attribution (a user move) is the same op with a null <see cref="SourceUid"/>.</summary>
public sealed record ItemLinkedPayload(string? SourceUid, string? Note, DateTimeOffset AddedAt);

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
