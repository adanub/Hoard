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

    public CurationService(IDbContextFactory<HoardDbContext> dbFactory, IMediaStore store)
    {
        _dbFactory = dbFactory;
        _store = store;
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

    /// <summary>Rename a board (collection) — its local name; source provenance is unchanged.</summary>
    public async Task RenameBoardAsync(int collectionId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("A board name is required.", nameof(newName));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return;
        collection.Name = newName.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a board (collection): removes the board grouping and its asset links (cascade). The assets
    /// themselves stay in the archive (still under "All images" and any other boards) — deleting their content
    /// is a separate, per-asset action.
    /// </summary>
    public async Task DeleteBoardAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct).ConfigureAwait(false);
        if (collection is null) return;
        db.Collections.Remove(collection); // cascade removes its CollectionItems
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
