using Hoard.Core.Domain;
using Hoard.Core.Ingest;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

/// <summary>
/// Write-side curation: removing assets the user no longer wants. Kept separate from <see cref="IngestService"/>
/// (which adds) and <see cref="LibraryService"/> (which only reads).
/// </summary>
public sealed class CurationService
{
    private readonly IDbContextFactory<HoardDbContext> _dbFactory;
    private readonly IMediaStore _store;
    private readonly IFileRecycler? _recycler;
    private readonly ArchiveLog _archive;

    public CurationService(
        IDbContextFactory<HoardDbContext> dbFactory, IMediaStore store, IFileRecycler? recycler = null,
        ArchiveLog? archive = null)
    {
        _dbFactory = dbFactory;
        _store = store;
        _recycler = recycler;
        // The null fallback (tests/headless) mints its own device id, so two service instances writing
        // the same DB can never collide on the unique (DeviceId, Seq) index.
        _archive = archive ?? new ArchiveLog(ArchiveLog.NewUid());
    }

    /// <summary>
    /// Curate an asset out by <b>tombstoning</b> it: keep the DB row (and its board links, so it still
    /// appears in place) but mark it deleted with the supplied note, free its blob from disk, and log a
    /// Remove op. The removal is therefore global (one row per unique content), recorded, and restorable.
    /// Returns the asset's SHA-256 (so callers can evict cached thumbnails), or null if it no longer
    /// exists or was already deleted.
    /// </summary>
    public async Task<string?> DeleteAssetAsync(int assetId, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("A deletion note is required.", nameof(note));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct).ConfigureAwait(false);
        if (asset is null || asset.DeletedAt is not null) return null;

        var sha = asset.Sha256;
        var relativePath = asset.RelativePath;

        // Tombstone the row + log the op atomically (board links and tags stay, so the tile remains).
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.DeletionNote = note.Trim();
        SyncLog.RecordRemove(db, sha);
        _archive.RecordAssetTombstoned(db, sha, asset.DeletionNote, asset.DeletedAt.Value);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);

        // Free the blob only after the commit, so a failure here leaves an orphan blob (harmless,
        // reclaimable) rather than a live row pointing at a missing file.
        await _store.DeleteAsync(relativePath, ct).ConfigureAwait(false);
        return sha;
    }

    /// <summary>
    /// Rename a board — a local <b>display-name override</b> (<see cref="Collection.DisplayName"/>); the source
    /// board's original name in <see cref="Collection.Name"/> and all provenance are left untouched, so the
    /// rename is non-destructive and survives re-imports.
    /// </summary>
    public async Task RenameBoardAsync(int collectionId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A board name is required.", nameof(newName));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return;
        collection.DisplayName = newName.Trim();
        _archive.RecordCollectionRenamed(db, collection);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove one merged source from a board (un-merge) and <b>delete its images completely</b>: the board's
    /// live images attributed to this source are removed outright (row + links gone everywhere) and their files
    /// sent to the OS recycle bin (recoverable there — not an in-app tombstone). When this is the board's
    /// <i>last</i> source, the board's remaining live images are swept too (catches pins imported before per-pin
    /// provenance). If the removed source was the board's denormalised primary pointer, re-point it at a
    /// surviving source (or clear it) so the two never drift. Returns how many images were deleted. No-op
    /// (returns 0) if the source is gone. A re-import of the board re-fetches the images fresh — nothing is kept
    /// to block that. (A separately tombstoned per-image delete is left alone — only live images are removed.)
    /// </summary>
    public async Task<int> RemoveSourceAsync(int collectionSourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var source = await db.CollectionSources
            .FirstOrDefaultAsync(s => s.Id == collectionSourceId, ct).ConfigureAwait(false);
        if (source is null) return 0;

        var lastSource = !await db.CollectionSources
            .AnyAsync(s => s.CollectionId == source.CollectionId && s.Id != source.Id, ct).ConfigureAwait(false);

        // The board's live images to remove: those attributed to this source — plus, when it's the board's last
        // source, any remaining (e.g. legacy un-attributed) pins, so nothing orphaned is left behind. Scoped to
        // the board's whole subtree so a source's pins filed into child folders (Pinterest sections) go with it
        // rather than being orphaned in a folder.
        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, source.CollectionId, ct).ConfigureAwait(false);
        var items = db.CollectionItems
            .Where(ci => subtreeIds.Contains(ci.CollectionId) && ci.Asset.DeletedAt == null);
        if (!lastSource)
            items = items.Where(ci => ci.CollectionSourceId == source.Id);
        var assets = await items.Select(ci => ci.Asset).Distinct().ToListAsync(ct).ConfigureAwait(false);

        var freed = StageAssetRemovals(db, assets);
        _archive.RecordSourceRemoved(db, source);
        db.CollectionSources.Remove(source); // (its links are gone with the assets; any others SET NULL by the FK)

        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == source.CollectionId, ct).ConfigureAwait(false);
        if (collection is not null
            && collection.SourceConnector == source.SourceConnector
            && collection.SourceBoardId == source.SourceBoardId)
        {
            // Re-seed the primary pointer from the next surviving source (by insert order) — null only if this
            // was the last one — so Collection.SourceBoardId/SourceUrl stays consistent with CollectionSources.
            var next = await db.CollectionSources
                .Where(s => s.CollectionId == source.CollectionId && s.Id != source.Id)
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            collection.SourceBoardId = next?.SourceBoardId;
            collection.SourceUrl = next?.SourceUrl;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        await FreeBlobsAsync(freed, ct).ConfigureAwait(false);
        return freed.Count;
    }

    /// <summary>
    /// Delete a board (collection) <b>and its images completely</b>, including its whole subtree of child folders
    /// (Pinterest sections / sub-folders): removes every live image across the subtree outright (row + links gone
    /// everywhere), sends their files to the OS recycle bin, and removes the board + descendant folders + their
    /// links (cascade). Returns how many images were deleted. No-op (returns 0) if the board is gone. A re-import
    /// re-fetches them fresh. (A separately tombstoned per-image delete is left alone.)
    /// </summary>
    public async Task<int> DeleteBoardAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return 0;

        // The board plus all descendant folders — deleting a board takes its whole subtree. The ParentId FK is
        // SET NULL (it won't cascade-delete children), so the descendant rows are gathered and removed explicitly.
        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, collectionId, ct).ConfigureAwait(false);

        var assets = await db.CollectionItems
            .Where(ci => subtreeIds.Contains(ci.CollectionId) && ci.Asset.DeletedAt == null)
            .Select(ci => ci.Asset)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var freed = StageAssetRemovals(db, assets);
        var collections = await db.Collections.Where(c => subtreeIds.Contains(c.Id)).ToListAsync(ct).ConfigureAwait(false);
        // Granular ops: one delete per collection in the subtree (replay removes each one's links + sources).
        foreach (var c in collections) _archive.RecordCollectionDeleted(db, c);
        db.Collections.RemoveRange(collections); // cascade removes each one's CollectionItems
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        await FreeBlobsAsync(freed, ct).ConfigureAwait(false);
        return freed.Count;
    }

    /// <summary>
    /// Move an asset's board link from one collection to another (e.g. file a loose pin into a folder, or move it
    /// back). Re-points the existing <see cref="CollectionItem"/> rather than adding a second link, so the asset
    /// lands in exactly one of the two. No-op if the asset isn't linked to <paramref name="fromCollectionId"/>;
    /// if it's already in <paramref name="toCollectionId"/>, the source link is simply dropped (the unique
    /// <c>(CollectionId, AssetId)</c> index allows only one). Manual filing detaches the pin from any per-source
    /// attribution, so it's the user's organisation from then on.
    /// </summary>
    public async Task MoveAssetWithinBoardAsync(
        int assetId, int fromCollectionId, int toCollectionId, CancellationToken ct = default)
    {
        if (fromCollectionId == toCollectionId) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);

        var link = await db.CollectionItems
            .FirstOrDefaultAsync(ci => ci.AssetId == assetId && ci.CollectionId == fromCollectionId, ct).ConfigureAwait(false);
        if (link is null) return;

        var destLink = await db.CollectionItems
            .FirstOrDefaultAsync(ci => ci.AssetId == assetId && ci.CollectionId == toCollectionId, ct).ConfigureAwait(false);
        if (destLink is not null)
        {
            // Already at the destination: drop the source link, and detach the surviving link's per-source
            // attribution too — once the user has filed it, it's their organisation, not auto-attributed (so a
            // later remove-source won't sweep it).
            db.CollectionItems.Remove(link);
            destLink.CollectionSourceId = null;
        }
        else
        {
            link.CollectionId = toCollectionId;
            link.CollectionSourceId = null; // now user-organised, not auto-attributed to a merged source
        }

        // Ops: unlink from the old board, (re-)link into the new one with the attribution cleared — the
        // item.linked upsert models both branches (the survivor keeps its own note/added-at).
        var sha = await db.Assets.Where(a => a.Id == assetId).Select(a => a.Sha256).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var from = await db.Collections.FirstOrDefaultAsync(c => c.Id == fromCollectionId, ct).ConfigureAwait(false);
        var to = await db.Collections.FirstOrDefaultAsync(c => c.Id == toCollectionId, ct).ConfigureAwait(false);
        if (sha is not null && from is not null && to is not null)
        {
            var survivor = destLink ?? link;
            _archive.RecordItemUnlinked(db, sha, ArchiveLog.UidOf(from));
            _archive.RecordItemLinked(db, sha, ArchiveLog.UidOf(to), sourceUid: null, survivor.Note, survivor.AddedAt);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage a hard delete of <paramref name="assets"/>: log a Remove op per asset (keyed by content SHA, the
    /// sync foundation) and remove its row — which cascades its board links + tags. Returns the blob paths to
    /// free <i>after</i> the commit (so a mid-way failure orphans a blob harmlessly rather than stranding a row).
    /// Does not call SaveChanges, so the caller can remove the owning source/board in the same transaction.
    /// </summary>
    private List<string> StageAssetRemovals(HoardDbContext db, IReadOnlyList<Asset> assets)
    {
        var blobs = new List<string>(assets.Count);
        foreach (var asset in assets)
        {
            SyncLog.RecordRemove(db, asset.Sha256);
            _archive.RecordAssetRemoved(db, asset.Sha256);
            blobs.Add(asset.RelativePath);
            db.Assets.Remove(asset);
        }
        return blobs;
    }

    /// <summary>
    /// Free freed blobs after the commit: to the OS recycle bin when a recycler is configured (recoverable),
    /// else a permanent delete (the test/headless fallback). A blob that can't be recycled (already gone) is
    /// skipped rather than failing the whole delete.
    /// </summary>
    private async Task FreeBlobsAsync(IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        if (relativePaths.Count == 0) return;
        if (_recycler is not null)
        {
            // One batched shell call for the whole set — far cheaper than recycling blob-by-blob on a large
            // board delete.
            var absolute = new List<string>(relativePaths.Count);
            foreach (var relativePath in relativePaths) absolute.Add(_store.GetAbsolutePath(relativePath));
            try { _recycler.RecycleFiles(absolute); }
            catch { /* a missing/locked blob shouldn't fail the delete */ }
            // The batch recycle removed the files out-of-band, so tidy the now-empty shard dirs it left behind
            // (the DeleteAsync path prunes as it goes; this matches it).
            _store.PruneEmptyShards(relativePaths);
            return;
        }
        foreach (var relativePath in relativePaths)
            await _store.DeleteAsync(relativePath, ct).ConfigureAwait(false);
    }
}
