using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Sync;

/// <summary>
/// Synthesises archive ops for the parts of a database that predate op emission, so the log becomes a
/// complete history of the archive (<c>SYNC-DESIGN.md</c>: this is the migration path for existing
/// projects, and the round-trip proof harness). Coverage-aware and therefore idempotent: rows already
/// described by an op (created after the v8 upgrade, or a previous synthesis run) are skipped, so it can
/// run at any point in a database's life. Ops are emitted through the same <see cref="ArchiveLog"/> as
/// live writes — one emission code path — with the clock driven by each row's own timestamp, so the
/// synthesised history carries the real historical order. The caller saves the context.
/// </summary>
public static class ArchiveOpSynthesiser
{
    public static async Task<int> SynthesiseAsync(HoardDbContext db, string deviceId, CancellationToken ct = default)
    {
        // What the log already covers — synthesis fills only the gaps. Assets/links are keyed by the
        // deterministic natural key (the payload's pin identity; a legacy op falls back to its sha
        // column), matching how replay resolves them.
        var existing = await db.ArchiveOps
            .Select(o => new { o.Kind, o.Sha256, o.EntityUid, o.PayloadJson })
            .ToListAsync(ct).ConfigureAwait(false);
        // NB the payload field names differ per kind: an added payload carries sourceConnector/sourceId,
        // a linked payload connector/sourceId — deserialize each with its own type.
        var coveredAssets = existing.Where(o => o.Kind == ArchiveOpKinds.AssetAdded)
            .Select(o =>
            {
                var p = o.PayloadJson is null ? null : ArchiveOpJson.Deserialize<AssetAddedPayload>(o.PayloadJson);
                return p?.SourceId is not null
                    ? ArchiveOpKeys.ForAsset(p.SourceConnector, p.SourceId, o.Sha256)
                    : ArchiveOpKeys.ForAsset(null, null, o.Sha256);
            }).ToHashSet();
        var coveredCollections = existing.Where(o => o.Kind == ArchiveOpKinds.CollectionCreated).Select(o => o.EntityUid!).ToHashSet();
        var coveredSources = existing.Where(o => o.Kind == ArchiveOpKinds.SourceAttached).Select(o => o.EntityUid!).ToHashSet();
        var coveredLinks = existing.Where(o => o.Kind == ArchiveOpKinds.ItemLinked && o.Sha256 != null && o.EntityUid != null)
            .Select(o =>
            {
                var p = o.PayloadJson is null ? null : ArchiveOpJson.Deserialize<ItemLinkedPayload>(o.PayloadJson);
                var key = p?.SourceId is not null
                    ? ArchiveOpKeys.ForAsset(p.Connector, p.SourceId, o.Sha256)
                    : ArchiveOpKeys.ForAsset(null, null, o.Sha256);
                return (key, o.EntityUid!);
            }).ToHashSet();
        static string RowAssetKey(Asset a) => ArchiveOpKeys.ForAsset(a.SourceConnector, a.SourceId, a.Sha256);

        // The clock follows each row's own timestamp; HybridClock keeps ticks monotonic regardless, so
        // the emission order below IS the synthesised causal order even across sloppy row timestamps.
        var cursor = DateTimeOffset.UnixEpoch;
        var log = new ArchiveLog(deviceId, () => cursor);
        await log.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var emitted = 0;

        // Collections, parents before children (a child op references its parent's uid). All ordering is
        // client-side: SQLite can't ORDER BY a DateTimeOffset column.
        var collections = (await db.Collections.ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        var done = new HashSet<int>();
        while (done.Count < collections.Count)
        {
            var progressed = false;
            foreach (var c in collections)
            {
                if (done.Contains(c.Id)) continue;
                if (c.ParentId is int pid && !done.Contains(pid) && collections.Any(x => x.Id == pid)) continue;
                done.Add(c.Id);
                progressed = true;
                if (coveredCollections.Contains(c.Uid ?? "")) continue;

                cursor = c.CreatedAt;
                log.RecordCollectionCreated(db, c, parentUid: c.Parent is { } parent ? ArchiveLog.UidOf(parent) : null);
                emitted++;
                if (c.DisplayName is not null)
                {
                    log.RecordCollectionRenamed(db, c);
                    emitted++;
                }
            }
            if (!progressed) break; // unreachable with FK-consistent data; never loop forever
        }

        foreach (var source in (await db.CollectionSources.Include(s => s.Collection).ToListAsync(ct).ConfigureAwait(false))
                     .OrderBy(s => s.AddedAt).ThenBy(s => s.Id))
        {
            if (coveredSources.Contains(source.Uid ?? "")) continue;
            cursor = source.AddedAt;
            log.RecordSourceAttached(db, source, source.Collection);
            emitted++;
        }

        foreach (var asset in (await db.Assets.Include(a => a.AssetTags).ThenInclude(at => at.Tag)
                     .ToListAsync(ct).ConfigureAwait(false))
                     .OrderBy(a => a.ImportedAt).ThenBy(a => a.Id))
        {
            if (coveredAssets.Contains(RowAssetKey(asset))) continue;
            cursor = asset.ImportedAt;
            log.RecordAssetAdded(db, asset, asset.AssetTags.Select(at => at.Tag.Name).ToList());
            emitted++;
            if (asset.DeletedAt is { } deletedAt)
            {
                cursor = deletedAt;
                log.RecordAssetTombstoned(db, asset);
                emitted++;
            }
        }

        foreach (var link in (await db.CollectionItems
                     .Include(ci => ci.Asset).Include(ci => ci.Collection).Include(ci => ci.CollectionSource)
                     .ToListAsync(ct).ConfigureAwait(false))
                     .OrderBy(ci => ci.AddedAt).ThenBy(ci => ci.Id))
        {
            // A LEGACY item.linked op carried no payload identity, so it keyed by sha — accept either
            // spelling as coverage, or a resumed migration would append a duplicate op per held link.
            if (coveredLinks.Contains((RowAssetKey(link.Asset), link.Collection.Uid ?? ""))
                || coveredLinks.Contains((ArchiveOpKeys.ForAsset(null, null, link.Asset.Sha256), link.Collection.Uid ?? ""))) continue;
            cursor = link.AddedAt;
            log.RecordItemLinked(db, link.Asset, ArchiveLog.UidOf(link.Collection),
                link.CollectionSource is { } source ? ArchiveLog.UidOf(source) : null, link.Note, link.AddedAt);
            emitted++;
        }

        return emitted;
    }
}
