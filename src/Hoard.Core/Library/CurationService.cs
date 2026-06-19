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

    public CurationService(IDbContextFactory<HoardDbContext> dbFactory, IMediaStore store, IFileRecycler? recycler = null)
    {
        _dbFactory = dbFactory;
        _store = store;
        _recycler = recycler;
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
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct).ConfigureAwait(false);
        if (asset is null || asset.DeletedAt is not null) return null;

        var sha = asset.Sha256;
        var relativePath = asset.RelativePath;

        // Tombstone the row + log the op atomically (board links and tags stay, so the tile remains).
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.DeletionNote = note.Trim();
        SyncLog.RecordRemove(db, sha);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

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
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return;
        collection.DisplayName = newName.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        var source = await db.CollectionSources
            .FirstOrDefaultAsync(s => s.Id == collectionSourceId, ct).ConfigureAwait(false);
        if (source is null) return 0;

        var lastSource = !await db.CollectionSources
            .AnyAsync(s => s.CollectionId == source.CollectionId && s.Id != source.Id, ct).ConfigureAwait(false);

        // The board's live images to remove: those attributed to this source — plus, when it's the board's last
        // source, any remaining (e.g. legacy un-attributed) pins, so nothing orphaned is left behind.
        var items = db.CollectionItems
            .Where(ci => ci.CollectionId == source.CollectionId && ci.Asset.DeletedAt == null);
        if (!lastSource)
            items = items.Where(ci => ci.CollectionSourceId == source.Id);
        var assets = await items.Select(ci => ci.Asset).Distinct().ToListAsync(ct).ConfigureAwait(false);

        var freed = StageAssetRemovals(db, assets);
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
        await FreeBlobsAsync(freed, ct).ConfigureAwait(false);
        return freed.Count;
    }

    /// <summary>
    /// Delete a board (collection) <b>and its images completely</b>: removes the board's live images outright
    /// (row + links gone everywhere), sends their files to the OS recycle bin, and removes the board grouping +
    /// its links (cascade). Returns how many images were deleted. No-op (returns 0) if the board is gone. A
    /// re-import re-fetches them fresh. (A separately tombstoned per-image delete is left alone.)
    /// </summary>
    public async Task<int> DeleteBoardAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return 0;

        var assets = await db.CollectionItems
            .Where(ci => ci.CollectionId == collectionId && ci.Asset.DeletedAt == null)
            .Select(ci => ci.Asset)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var freed = StageAssetRemovals(db, assets);
        db.Collections.Remove(collection); // cascade removes its CollectionItems
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await FreeBlobsAsync(freed, ct).ConfigureAwait(false);
        return freed.Count;
    }

    /// <summary>
    /// Stage a hard delete of <paramref name="assets"/>: log a Remove op per asset (keyed by content SHA, the
    /// sync foundation) and remove its row — which cascades its board links + tags. Returns the blob paths to
    /// free <i>after</i> the commit (so a mid-way failure orphans a blob harmlessly rather than stranding a row).
    /// Does not call SaveChanges, so the caller can remove the owning source/board in the same transaction.
    /// </summary>
    private static List<string> StageAssetRemovals(HoardDbContext db, IReadOnlyList<Asset> assets)
    {
        var blobs = new List<string>(assets.Count);
        foreach (var asset in assets)
        {
            SyncLog.RecordRemove(db, asset.Sha256);
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
            // board delete. (Empty shard dirs aren't pruned on this path — harmless.)
            var absolute = new List<string>(relativePaths.Count);
            foreach (var relativePath in relativePaths) absolute.Add(_store.GetAbsolutePath(relativePath));
            try { _recycler.RecycleFiles(absolute); }
            catch { /* a missing/locked blob shouldn't fail the delete */ }
            return;
        }
        foreach (var relativePath in relativePaths)
            await _store.DeleteAsync(relativePath, ct).ConfigureAwait(false);
    }
}
