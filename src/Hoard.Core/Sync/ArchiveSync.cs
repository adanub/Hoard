using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Sync;

/// <summary>
/// P2 reconciliation (<c>SYNC-DESIGN.md</c>): bring this machine's database and the project's op
/// segments into agreement. Our own segment is backfilled from the authoritative table
/// (<see cref="ArchiveLog.FlushSegmentAsync"/>); FOREIGN segments — another device's ops on a shared
/// project folder — are caught up here: every segment op we don't yet hold (a per-device set difference
/// against our <c>ArchiveOps</c> table — hole-tolerant, see <see cref="CatchUpAsync"/>) is applied to
/// the live database and recorded in the table in the same save, atomically. Foreign ops are
/// applied in their device's seq order; the catalogue's ops are shaped to converge under any
/// cross-device interleaving (adds/links upsert, removals are idempotent, LWW fields overwrite).
/// </summary>
public static class ArchiveSync
{
    /// <summary>Open-time reconcile: seed the log, backfill our own segment, catch up foreign ones.</summary>
    public static async Task SyncAtOpenAsync(HoardDbContext db, string opsRoot, ArchiveLog archive, ILogger? logger = null, CancellationToken ct = default)
    {
        await archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        await archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        await CatchUpAsync(db, opsRoot, archive, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply segments' new ops to the live database. This deliberately includes OUR OWN segment: in
    /// steady state it's a no-op (every own op is already in the table), but on a fresh or deleted index
    /// (a new machine, or per-machine state wiped) it's what replays this device's own history back in —
    /// the index is fully derivable from the archive alone. All segments' pending ops are merged into
    /// ONE stream ordered by (HLC, device, seq) before applying: a device only references entities it
    /// has already observed, so HLC order is causally consistent — whereas applying segment-by-segment
    /// would silently drop an op whose target lives in a not-yet-applied device's segment. Returns how
    /// many ops were applied.
    /// </summary>
    public static async Task<int> CatchUpAsync(
        HoardDbContext db, string opsRoot, ArchiveLog archive, ILogger? logger = null, CancellationToken ct = default)
    {
        var pending = new List<ArchiveOp>();
        foreach (var (deviceId, path) in ArchiveSegments.ListSegments(opsRoot))
        {
            // Pending = set difference against the rows we hold, NOT a MAX(Seq) high-water mark: a
            // batch rollback (crash/cancel mid-catch-up) can leave a committed seq BEYOND a hole — the
            // in-memory counter deliberately mints past the whole segment history, so a local op can
            // commit at seq 1000 while rolled-back segment ops 501–749 are absent — and a high-water
            // mark would bury the hole forever. The difference re-pends exactly the missing rows.
            var have = (await db.ArchiveOps.Where(o => o.DeviceId == deviceId)
                .Select(o => o.Seq).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
            pending.AddRange(ArchiveSegments.Read(path, deviceId).Where(o => !have.Contains(o.Seq)));
        }
        if (pending.Count == 0) return 0;

        // Each op's row and its effect share a SaveChanges (later ops' lookups query the database, so
        // they must see earlier ops' rows), but the saves are grouped into batched transactions: a
        // per-op commit is an fsync each, which made a big archive's first index build IO-bound. An
        // interrupted batch rolls back whole and the set difference above re-pends it next open; the
        // clock/seq observations running ahead of a rolled-back batch is harmless (both only need to
        // stay ahead). The tracker is cleared at every commit — without that, the tracked set grows
        // with the whole archive and per-save DetectChanges turns a big first build O(N²).
        const int batchSize = 500;
        var applied = 0;
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var op in pending
                         .OrderBy(o => o.Hlc, StringComparer.Ordinal)
                         .ThenBy(o => o.DeviceId, StringComparer.Ordinal)
                         .ThenBy(o => o.Seq))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ApplyAsync(db, op, ct).ConfigureAwait(false);
                    db.ArchiveOps.Add(op); // the op row commits atomically with its effect
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad op (garbled-but-parsable payload, an unexpected constraint) must not
                    // wedge the catch-up or poison its batch — the same stance as unknown kinds: skip
                    // it, permanently. SaveChanges rolls back to its own savepoint on failure, so the
                    // transaction and every earlier op in it survive; drop this op's partial effects
                    // and record the op row alone so the skip is remembered instead of retried forever.
                    db.ChangeTracker.Clear();
                    logger?.LogWarning(ex, "Skipped archive op {Device}#{Seq} ({Kind}): it could not be applied.",
                        op.DeviceId, op.Seq, op.Kind);
                    try
                    {
                        db.ArchiveOps.Add(op);
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception recordEx) when (recordEx is not OperationCanceledException)
                    {
                        db.ChangeTracker.Clear();
                        logger?.LogWarning(recordEx, "Couldn't record skipped op {Device}#{Seq}; it will re-pend next open.",
                            op.DeviceId, op.Seq);
                        continue;
                    }
                }
                archive.Observe(op.Hlc);    // our next local op orders after everything we've seen…
                if (op.DeviceId == archive.DeviceId) archive.ObserveOwnSeq(op.Seq); // …and never re-mints a replayed seq
                if (++applied % batchSize == 0)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    await tx.DisposeAsync().ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                    tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }
        return pending.Count;
    }

    /// <summary>
    /// Apply one op against the index database — the ONE apply semantics, shared by foreign catch-up
    /// and full rebuilds (a fresh index replays every segment through here). Idempotent per op; an op
    /// whose target is absent (already removed locally) or whose kind is unknown (a newer device) is
    /// skipped, never an error.
    /// </summary>
    private static async Task ApplyAsync(HoardDbContext db, ArchiveOp op, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case ArchiveOpKinds.AssetAdded when op.Sha256 is not null:
            {
                if (await db.Assets.AnyAsync(a => a.Sha256 == op.Sha256, ct).ConfigureAwait(false)) break;
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
                foreach (var name in p.Tags ?? [])
                {
                    var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct).ConfigureAwait(false)
                              ?? db.Tags.Local.FirstOrDefault(t => t.Name == name);
                    if (tag is null)
                    {
                        tag = new Tag { Name = name };
                        db.Tags.Add(tag);
                    }
                    asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
                }
                break;
            }
            case ArchiveOpKinds.AssetTombstoned when await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false) is { } asset:
            {
                var p = ArchiveOpJson.Deserialize<AssetTombstonedPayload>(op.PayloadJson);
                asset.DeletedAt = p.DeletedAt;
                asset.DeletionNote = p.Note;
                break;
            }
            case ArchiveOpKinds.AssetRestored when await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false) is { } asset:
            {
                await ApplyContentChangeAsync(db, asset, op, ct).ConfigureAwait(false);
                asset.DeletedAt = null;
                asset.DeletionNote = null;
                break;
            }
            case ArchiveOpKinds.AssetRefetched when await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false) is { } asset:
                await ApplyContentChangeAsync(db, asset, op, ct).ConfigureAwait(false);
                break;
            case ArchiveOpKinds.AssetRetagged when await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false) is { } asset:
            {
                // Full replacement of the asset's tag set (LWW). Delete-then-insert of the same
                // (AssetId, TagId) pair in one save is fine — EF orders deletes before inserts.
                var p = ArchiveOpJson.Deserialize<AssetRetaggedPayload>(op.PayloadJson);
                var current = await db.AssetTags.Where(at => at.AssetId == asset.Id).ToListAsync(ct).ConfigureAwait(false);
                db.AssetTags.RemoveRange(current);
                foreach (var name in p.Tags)
                {
                    var tag = db.Tags.Local.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                              ?? await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct).ConfigureAwait(false);
                    if (tag is null)
                    {
                        tag = new Tag { Name = name };
                        db.Tags.Add(tag);
                    }
                    db.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
                }
                break;
            }
            case ArchiveOpKinds.AssetRemoved when await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false) is { } asset:
                db.Assets.Remove(asset); // links + tags cascade
                break;
            case ArchiveOpKinds.CollectionCreated when op.EntityUid is not null:
            {
                if (await db.Collections.AnyAsync(c => c.Uid == op.EntityUid, ct).ConfigureAwait(false)) break;
                var p = ArchiveOpJson.Deserialize<CollectionCreatedPayload>(op.PayloadJson);
                var parent = p.ParentUid is not null
                    ? await db.Collections.FirstOrDefaultAsync(c => c.Uid == p.ParentUid, ct).ConfigureAwait(false)
                    : null;
                db.Collections.Add(new Collection
                {
                    Uid = op.EntityUid,
                    Name = p.Name,
                    SourceConnector = p.SourceConnector,
                    SourceBoardId = p.SourceBoardId,
                    SourceUrl = p.SourceUrl,
                    SourceSectionId = p.SourceSectionId,
                    CreatedAt = p.CreatedAt,
                    Parent = parent,
                });
                break;
            }
            case ArchiveOpKinds.CollectionRenamed when await FindCollectionAsync(db, op.EntityUid, ct).ConfigureAwait(false) is { } collection:
            {
                var p = ArchiveOpJson.Deserialize<CollectionRenamedPayload>(op.PayloadJson);
                collection.DisplayName = string.IsNullOrEmpty(p.DisplayName) ? null : p.DisplayName;
                break;
            }
            case ArchiveOpKinds.CollectionDeleted when await FindCollectionAsync(db, op.EntityUid, ct).ConfigureAwait(false) is { } collection:
                db.Collections.Remove(collection); // links + sources cascade; children arrive as their own ops
                break;
            case ArchiveOpKinds.SourceAttached when op.EntityUid is not null:
            {
                if (await db.CollectionSources.AnyAsync(s => s.Uid == op.EntityUid, ct).ConfigureAwait(false)) break;
                var p = ArchiveOpJson.Deserialize<SourceAttachedPayload>(op.PayloadJson);
                var owner = await db.Collections.FirstOrDefaultAsync(c => c.Uid == p.CollectionUid, ct).ConfigureAwait(false);
                if (owner is null) break;
                // The (collection, connector, board) uniqueness can already be satisfied locally (both
                // machines merged the same source board independently) — the local record stands.
                var duplicate = await db.CollectionSources.AnyAsync(
                    s => s.CollectionId == owner.Id && s.SourceConnector == p.SourceConnector && s.SourceBoardId == p.SourceBoardId,
                    ct).ConfigureAwait(false);
                if (duplicate) break;
                db.CollectionSources.Add(new CollectionSource
                {
                    Uid = op.EntityUid,
                    Collection = owner,
                    SourceConnector = p.SourceConnector,
                    SourceBoardId = p.SourceBoardId,
                    SourceUrl = p.SourceUrl,
                    Name = p.Name,
                    AddedAt = p.AddedAt,
                });
                if (owner.SourceBoardId is null)
                {
                    owner.SourceBoardId = p.SourceBoardId;
                    owner.SourceUrl = p.SourceUrl;
                }
                break;
            }
            case ArchiveOpKinds.SourceUpdated:
            {
                var source = await db.CollectionSources
                    .FirstOrDefaultAsync(s => s.Uid == op.EntityUid, ct).ConfigureAwait(false);
                if (source is not null)
                    source.SourceUrl = ArchiveOpJson.Deserialize<SourceUpdatedPayload>(op.PayloadJson).SourceUrl;
                break;
            }
            case ArchiveOpKinds.SourceRemoved:
            {
                var source = await db.CollectionSources.Include(s => s.Collection)
                    .FirstOrDefaultAsync(s => s.Uid == op.EntityUid, ct).ConfigureAwait(false);
                if (source is null) break;
                var owner = source.Collection;
                db.CollectionSources.Remove(source); // attributed links SET NULL by the FK
                if (owner.SourceConnector == source.SourceConnector && owner.SourceBoardId == source.SourceBoardId)
                {
                    var next = await db.CollectionSources
                        .Where(s => s.CollectionId == owner.Id && s.Id != source.Id)
                        .ToListAsync(ct).ConfigureAwait(false);
                    var survivor = next.OrderBy(s => s.AddedAt).ThenBy(s => s.Uid, StringComparer.Ordinal).FirstOrDefault();
                    owner.SourceBoardId = survivor?.SourceBoardId;
                    owner.SourceUrl = survivor?.SourceUrl;
                }
                break;
            }
            case ArchiveOpKinds.ItemLinked when op.Sha256 is not null && op.EntityUid is not null:
            {
                var asset = await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false);
                var collection = await FindCollectionAsync(db, op.EntityUid, ct).ConfigureAwait(false);
                if (asset is null || collection is null) break;
                var p = ArchiveOpJson.Deserialize<ItemLinkedPayload>(op.PayloadJson);
                var source = p.SourceUid is not null
                    ? await db.CollectionSources.FirstOrDefaultAsync(s => s.Uid == p.SourceUid, ct).ConfigureAwait(false)
                    : null;
                var existing = await db.CollectionItems
                    .FirstOrDefaultAsync(ci => ci.CollectionId == collection.Id && ci.AssetId == asset.Id, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    existing.CollectionSource = source;
                    if (source is null) existing.CollectionSourceId = null;
                    existing.Note = p.Note;
                    existing.AddedAt = p.AddedAt;
                    break;
                }
                db.CollectionItems.Add(new CollectionItem
                {
                    Collection = collection,
                    Asset = asset,
                    CollectionSource = source,
                    Note = p.Note,
                    AddedAt = p.AddedAt,
                });
                break;
            }
            case ArchiveOpKinds.ItemUnlinked when op.Sha256 is not null && op.EntityUid is not null:
            {
                var asset = await FindAssetAsync(db, op.Sha256, ct).ConfigureAwait(false);
                var collection = await FindCollectionAsync(db, op.EntityUid, ct).ConfigureAwait(false);
                if (asset is null || collection is null) break;
                var link = await db.CollectionItems
                    .FirstOrDefaultAsync(ci => ci.CollectionId == collection.Id && ci.AssetId == asset.Id, ct).ConfigureAwait(false);
                if (link is not null) db.CollectionItems.Remove(link);
                break;
            }
        }
    }

    private static async Task ApplyContentChangeAsync(HoardDbContext db, Asset asset, ArchiveOp op, CancellationToken ct)
    {
        var p = ArchiveOpJson.Deserialize<AssetContentChangedPayload>(op.PayloadJson);
        // If the new content identity already exists as another row, adopting it would violate the
        // unique sha index — keep the local row as-is (the known RestoreAsync sha-collision wart).
        if (p.Sha256 != asset.Sha256
            && await db.Assets.AnyAsync(a => a.Sha256 == p.Sha256, ct).ConfigureAwait(false))
            return;
        asset.Sha256 = p.Sha256;
        asset.RelativePath = p.RelativePath;
        asset.Bytes = p.Bytes;
    }

    private static Task<Asset?> FindAssetAsync(HoardDbContext db, string? sha, CancellationToken ct) =>
        sha is null ? Task.FromResult<Asset?>(null) : db.Assets.FirstOrDefaultAsync(a => a.Sha256 == sha, ct);

    private static Task<Collection?> FindCollectionAsync(HoardDbContext db, string? uid, CancellationToken ct) =>
        uid is null ? Task.FromResult<Collection?>(null) : db.Collections.FirstOrDefaultAsync(c => c.Uid == uid, ct);
}
