using Hoard.Core.Domain;
using Hoard.Core.Metadata;

namespace Hoard.Core.Sync;

/// <summary>
/// Materialises a metadata database from the archive op log (<c>SYNC-DESIGN.md</c>): the proof that the
/// log alone carries the whole archive, and — at P3 — the way a machine builds/refreshes its local index.
/// Ops apply in one deterministic total order (HLC, then device, then seq — HLC strings compare ordinally
/// by construction), into an <b>empty</b> context's change tracker, saved once at the end. Unknown kinds
/// are ignored (a newer device's ops must not break an older reader); ops whose target is absent are
/// skipped the same way.
/// </summary>
public static class ArchiveRebuilder
{
    public static async Task RebuildAsync(HoardDbContext target, IEnumerable<ArchiveOp> ops, CancellationToken ct = default)
    {
        var state = new State();
        foreach (var op in ops.OrderBy(o => o.Hlc, StringComparer.Ordinal).ThenBy(o => o.DeviceId, StringComparer.Ordinal).ThenBy(o => o.Seq))
        {
            ct.ThrowIfCancellationRequested();
            Apply(target, state, op);
        }
        await target.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private sealed class State
    {
        public readonly Dictionary<string, Asset> Assets = new();                       // by sha
        public readonly Dictionary<string, Collection> Collections = new();             // by uid
        public readonly Dictionary<string, CollectionSource> Sources = new();           // by uid
        public readonly Dictionary<(string Sha, string ColUid), CollectionItem> Links = new();
        public readonly Dictionary<string, Tag> Tags = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void Apply(HoardDbContext db, State s, ArchiveOp op)
    {
        switch (op.Kind)
        {
            case ArchiveOpKinds.AssetAdded when op.Sha256 is not null && !s.Assets.ContainsKey(op.Sha256):
            {
                var p = ArchiveOpJson.Deserialize<AssetAddedPayload>(op.PayloadJson);
                var asset = new Asset
                {
                    Sha256 = op.Sha256,
                    RelativePath = p.RelativePath,
                    MimeType = p.MimeType,
                    Kind = p.Kind,
                    Width = p.Width,
                    Height = p.Height,
                    Bytes = p.Bytes,
                    SourceConnector = p.SourceConnector,
                    SourceId = p.SourceId,
                    SourceUrl = p.SourceUrl,
                    OriginalUrl = p.OriginalUrl,
                    Title = p.Title,
                    Description = p.Description,
                    MetadataJson = p.MetadataJson,
                    CreatedAt = p.CreatedAt,
                    ImportedAt = p.ImportedAt,
                };
                db.Assets.Add(asset);
                s.Assets[op.Sha256] = asset;
                foreach (var name in p.Tags ?? [])
                {
                    if (!s.Tags.TryGetValue(name, out var tag))
                    {
                        tag = new Tag { Name = name };
                        db.Tags.Add(tag);
                        s.Tags[name] = tag;
                    }
                    asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
                }
                break;
            }
            case ArchiveOpKinds.AssetTombstoned when Find(s.Assets, op.Sha256) is { } asset:
            {
                var p = ArchiveOpJson.Deserialize<AssetTombstonedPayload>(op.PayloadJson);
                asset.DeletedAt = p.DeletedAt;
                asset.DeletionNote = p.Note;
                break;
            }
            case ArchiveOpKinds.AssetRestored when Find(s.Assets, op.Sha256) is { } asset:
            {
                ApplyContentChange(s, asset, op);
                asset.DeletedAt = null;
                asset.DeletionNote = null;
                break;
            }
            case ArchiveOpKinds.AssetRefetched when Find(s.Assets, op.Sha256) is { } asset:
                ApplyContentChange(s, asset, op);
                break;
            case ArchiveOpKinds.AssetRetagged when Find(s.Assets, op.Sha256) is { } asset:
            {
                // Full replacement of the asset's tag set (LWW).
                var p = ArchiveOpJson.Deserialize<AssetRetaggedPayload>(op.PayloadJson);
                foreach (var at in asset.AssetTags.ToList()) Drop(db, at);
                asset.AssetTags.Clear();
                foreach (var name in p.Tags)
                {
                    if (!s.Tags.TryGetValue(name, out var tag))
                    {
                        tag = new Tag { Name = name };
                        db.Tags.Add(tag);
                        s.Tags[name] = tag;
                    }
                    asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
                }
                break;
            }
            case ArchiveOpKinds.AssetRemoved when Find(s.Assets, op.Sha256) is { } asset:
            {
                foreach (var (key, link) in s.Links.Where(kv => kv.Key.Sha == asset.Sha256).ToList())
                {
                    s.Links.Remove(key);
                    Drop(db, link);
                }
                s.Assets.Remove(asset.Sha256);
                foreach (var at in asset.AssetTags.ToList()) Drop(db, at);
                Drop(db, asset);
                break;
            }
            case ArchiveOpKinds.CollectionCreated when op.EntityUid is not null && !s.Collections.ContainsKey(op.EntityUid):
            {
                var p = ArchiveOpJson.Deserialize<CollectionCreatedPayload>(op.PayloadJson);
                var collection = new Collection
                {
                    Uid = op.EntityUid,
                    Name = p.Name,
                    SourceConnector = p.SourceConnector,
                    SourceBoardId = p.SourceBoardId,
                    SourceUrl = p.SourceUrl,
                    SourceSectionId = p.SourceSectionId,
                    CreatedAt = p.CreatedAt,
                    Parent = p.ParentUid is not null && s.Collections.TryGetValue(p.ParentUid, out var parent) ? parent : null,
                };
                db.Collections.Add(collection);
                s.Collections[op.EntityUid] = collection;
                break;
            }
            case ArchiveOpKinds.CollectionRenamed when Find(s.Collections, op.EntityUid) is { } collection:
            {
                var p = ArchiveOpJson.Deserialize<CollectionRenamedPayload>(op.PayloadJson);
                collection.DisplayName = string.IsNullOrEmpty(p.DisplayName) ? null : p.DisplayName;
                break;
            }
            case ArchiveOpKinds.CollectionDeleted when Find(s.Collections, op.EntityUid) is { } collection:
            {
                // Granular writers delete a subtree one op per collection, so only this one goes — with
                // its links and sources (the FK cascades' equivalent).
                foreach (var (key, link) in s.Links.Where(kv => kv.Key.ColUid == collection.Uid).ToList())
                {
                    s.Links.Remove(key);
                    Drop(db, link);
                }
                foreach (var source in collection.Sources.ToList())
                {
                    if (source.Uid is not null) s.Sources.Remove(source.Uid);
                    Drop(db, source);
                }
                s.Collections.Remove(collection.Uid!);
                Drop(db, collection);
                break;
            }
            case ArchiveOpKinds.SourceAttached when op.EntityUid is not null && !s.Sources.ContainsKey(op.EntityUid):
            {
                var p = ArchiveOpJson.Deserialize<SourceAttachedPayload>(op.PayloadJson);
                if (!s.Collections.TryGetValue(p.CollectionUid, out var owner)) break;
                var source = new CollectionSource
                {
                    Uid = op.EntityUid,
                    Collection = owner,
                    SourceConnector = p.SourceConnector,
                    SourceBoardId = p.SourceBoardId,
                    SourceUrl = p.SourceUrl,
                    Name = p.Name,
                    AddedAt = p.AddedAt,
                };
                owner.Sources.Add(source);
                db.CollectionSources.Add(source);
                s.Sources[op.EntityUid] = source;
                // First source seen seeds the board's denormalised primary pointer — the import rule.
                if (owner.SourceBoardId is null)
                {
                    owner.SourceBoardId = p.SourceBoardId;
                    owner.SourceUrl = p.SourceUrl;
                }
                break;
            }
            case ArchiveOpKinds.SourceUpdated when Find(s.Sources, op.EntityUid) is { } source:
                source.SourceUrl = ArchiveOpJson.Deserialize<SourceUpdatedPayload>(op.PayloadJson).SourceUrl;
                break;
            case ArchiveOpKinds.SourceRemoved when Find(s.Sources, op.EntityUid) is { } source:
            {
                var owner = source.Collection;
                foreach (var link in s.Links.Values.Where(l => ReferenceEquals(l.CollectionSource, source)))
                    link.CollectionSource = null; // the FK's SET NULL
                owner.Sources.Remove(source);
                s.Sources.Remove(source.Uid!);
                Drop(db, source);
                // Re-seed the primary pointer from the earliest surviving source — the service's rule
                // (next by insert order), expressed in op terms as (AddedAt, uid).
                if (owner.SourceConnector == source.SourceConnector && owner.SourceBoardId == source.SourceBoardId)
                {
                    var next = owner.Sources.OrderBy(x => x.AddedAt).ThenBy(x => x.Uid, StringComparer.Ordinal).FirstOrDefault();
                    owner.SourceBoardId = next?.SourceBoardId;
                    owner.SourceUrl = next?.SourceUrl;
                }
                break;
            }
            case ArchiveOpKinds.ItemLinked when op.Sha256 is not null && op.EntityUid is not null:
            {
                if (!s.Assets.TryGetValue(op.Sha256, out var asset) || !s.Collections.TryGetValue(op.EntityUid, out var collection))
                    break;
                var p = ArchiveOpJson.Deserialize<ItemLinkedPayload>(op.PayloadJson);
                var source = p.SourceUid is not null && s.Sources.TryGetValue(p.SourceUid, out var src) ? src : null;
                if (s.Links.TryGetValue((op.Sha256, op.EntityUid), out var existing))
                {
                    // Upsert: a later link op (e.g. a user move clearing the attribution) wins.
                    existing.CollectionSource = source;
                    existing.Note = p.Note;
                    existing.AddedAt = p.AddedAt;
                    break;
                }
                var link = new CollectionItem
                {
                    Collection = collection,
                    Asset = asset,
                    CollectionSource = source,
                    Note = p.Note,
                    AddedAt = p.AddedAt,
                };
                collection.Items.Add(link);
                asset.CollectionItems.Add(link);
                s.Links[(op.Sha256, op.EntityUid)] = link;
                break;
            }
            case ArchiveOpKinds.ItemUnlinked when op.Sha256 is not null && op.EntityUid is not null
                                                  && s.Links.Remove((op.Sha256, op.EntityUid), out var link):
            {
                link.Collection.Items.Remove(link);
                link.Asset.CollectionItems.Remove(link);
                Drop(db, link);
                break;
            }
        }
    }

    private static void ApplyContentChange(State s, Asset asset, ArchiveOp op)
    {
        var p = ArchiveOpJson.Deserialize<AssetContentChangedPayload>(op.PayloadJson);
        if (p.Sha256 != asset.Sha256)
        {
            // The re-download yielded different bytes: the asset's identity moved, so its keys move too.
            s.Assets.Remove(asset.Sha256);
            s.Assets[p.Sha256] = asset;
            foreach (var (key, link) in s.Links.Where(kv => kv.Key.Sha == asset.Sha256).ToList())
            {
                s.Links.Remove(key);
                s.Links[(p.Sha256, key.ColUid)] = link;
            }
            asset.Sha256 = p.Sha256;
        }
        asset.RelativePath = p.RelativePath;
        asset.Bytes = p.Bytes;
    }

    private static T? Find<T>(Dictionary<string, T> map, string? key) where T : class =>
        key is not null && map.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Remove an entity from the rebuild. Everything in a rebuild is still unsaved (Added), so removal
    /// means detaching — <c>DbContext.Remove</c> would try to flip it to Deleted on a temporary key. The
    /// Deleted arm only matters if a rebuild ever runs over a non-empty context.
    /// </summary>
    private static void Drop(HoardDbContext db, object entity)
    {
        var entry = db.Entry(entity);
        entry.State = entry.State == Microsoft.EntityFrameworkCore.EntityState.Added
            ? Microsoft.EntityFrameworkCore.EntityState.Detached
            : Microsoft.EntityFrameworkCore.EntityState.Deleted;
    }
}
