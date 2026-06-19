using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoard.Core.Ingest;

/// <summary>
/// Orchestrates an import: pick a connector for the URL, download via gallery-dl, copy each blob
/// into the content-addressed store (dedup by hash), and upsert assets/boards/tags in the DB.
/// </summary>
public sealed class IngestService
{
    private readonly IDbContextFactory<HoardDbContext> _dbFactory;
    private readonly IMediaStore _store;
    private readonly IReadOnlyList<ISourceConnector> _connectors;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IDbContextFactory<HoardDbContext> dbFactory,
        IMediaStore store,
        IEnumerable<ISourceConnector> connectors,
        ILogger<IngestService>? logger = null)
    {
        _dbFactory = dbFactory;
        _store = store;
        _connectors = connectors.ToList();
        _logger = logger ?? NullLogger<IngestService>.Instance;
    }

    /// <summary>
    /// Create an empty local board (collection) up front — so an import has somewhere to land immediately and
    /// its card can show progress from the first pin — and return its id. The connector is resolved from the
    /// <paramref name="url"/> the board will be filled from.
    /// </summary>
    public async Task<int> CreateBoardAsync(string name, string url, CancellationToken ct = default)
    {
        var connectorName = _connectors.FirstOrDefault(c => c.CanHandle(url))?.Name ?? "";
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var collection = new Collection
        {
            Name = name,
            SourceConnector = connectorName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return collection.Id;
    }

    /// <param name="targetCollectionId">When set, every imported pin is linked into this one board (the board
    /// the user chose/created), instead of auto-foldering by each pin's source board.</param>
    public async Task<IngestResult> ImportAsync(
        string url, ConnectorOptions options, IProgress<IngestProgress>? progress,
        int? targetCollectionId = null, CancellationToken ct = default)
    {
        var connector = _connectors.FirstOrDefault(c => c.CanHandle(url))
            ?? throw new NotSupportedException($"No connector can handle '{url}'.");

        progress?.Report(new IngestProgress(IngestPhase.Starting, 0, 0, $"Starting {connector.Name} download…"));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var targetCollection = targetCollectionId is int tid
            ? await db.Collections.FirstOrDefaultAsync(c => c.Id == tid, ct).ConfigureAwait(false)
            : null;

        // Local caches so repeated keys within a single import resolve before SaveChanges.
        var assetsBySha = new Dictionary<string, Asset>();
        var collections = new Dictionary<string, Collection>();
        var tags = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);

        int processed = 0, newAssets = 0, duplicates = 0, skippedDeleted = 0;

        var downloadLog = new Progress<string>(line =>
            progress?.Report(new IngestProgress(IngestPhase.Downloading, processed, 0, line)));

        // Hand the connector the full set the library already tracks (live AND tombstoned) so it can
        // rebuild its skip-archive from this single source of truth — the archive never drifts from the DB.
        var effectiveOptions = options with { KnownItems = await GetKnownItemsAsync(db, ct).ConfigureAwait(false) };

        // Each item is ingested the moment the connector finishes downloading it, and the freshly
        // imported asset is reported so the UI can show it immediately (not after the whole batch).
        await connector.DownloadAsync(url, effectiveOptions, downloadLog, async (item, itemCt) =>
        {
            var blob = await _store.PutAsync(item.FilePath, itemCt).ConfigureAwait(false);
            var existing = await FindExistingAssetAsync(db, assetsBySha, blob.Sha256, itemCt).ConfigureAwait(false);

            // Honour a tombstone: this content was deliberately deleted, so don't resurrect it (and drop
            // the blob we just re-stored). The DB is the authority even if the skip-archive missed it.
            if (existing is { DeletedAt: not null })
            {
                await _store.DeleteAsync(blob.RelativePath, itemCt).ConfigureAwait(false);
                processed++;
                skippedDeleted++;
                progress?.Report(new IngestProgress(IngestPhase.Storing, processed, 0,
                    $"Skipped (deleted): {item.Title ?? item.SourceId ?? Path.GetFileName(item.FilePath)}"));
                return;
            }

            var isNew = existing is null;
            var asset = existing ?? CreateAsset(db, assetsBySha, blob, item);

            Collection? collection;
            if (targetCollection is not null)
            {
                // Import into the one board the user chose/created. Seed its source ref from the first pin that
                // has one, so future re-imports of that board can skip already-fetched pins (GetKnownItems).
                collection = targetCollection;
                if (collection.SourceBoardId is null && item.BoardId is not null)
                {
                    collection.SourceBoardId = item.BoardId;
                    collection.SourceUrl = item.BoardUrl;
                }
            }
            else
            {
                collection = await GetOrCreateCollectionAsync(db, collections, item, connector.Name, itemCt).ConfigureAwait(false);
            }
            if (collection is not null)
                await LinkToCollectionAsync(db, collection, asset, item, itemCt).ConfigureAwait(false);

            await AttachTagsAsync(db, tags, asset, item, itemCt).ConfigureAwait(false);

            // Persist per item so the asset gets an Id and is immediately queryable/displayable.
            await db.SaveChangesAsync(itemCt).ConfigureAwait(false);

            processed++;
            if (isNew) newAssets++; else duplicates++;

            var view = isNew ? ToView(asset) : null; // only surface genuinely new images to the grid
            progress?.Report(new IngestProgress(IngestPhase.Storing, processed, 0,
                $"Imported {processed} — {item.Title ?? item.SourceId ?? Path.GetFileName(item.FilePath)}", view));
        }, ct).ConfigureAwait(false);

        var skippedNote = skippedDeleted > 0 ? $", {skippedDeleted} skipped (deleted)" : "";
        progress?.Report(new IngestProgress(IngestPhase.Done, processed, processed,
            $"Imported {newAssets} new, {duplicates} already-had{skippedNote}."));
        var touched = targetCollection is not null ? 1 : collections.Count;
        return new IngestResult(processed, newAssets, duplicates, touched);
    }

    // One shared client for restore downloads (the recommended HttpClient usage).
    private static readonly HttpClient RestoreHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>
    /// Un-delete a tombstoned asset by re-downloading its original media directly from the stored media URL
    /// (the public CDN link gallery-dl saved — no cookies or subprocess needed), re-storing the blob,
    /// clearing the tombstone, and logging an Add. Returns the restored <see cref="AssetView"/>, or null if
    /// the asset is missing or not deleted.
    /// </summary>
    public async Task<AssetView?> RestoreAsync(int assetId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct).ConfigureAwait(false);
        if (asset is null || asset.DeletedAt is null) return null;

        if (string.IsNullOrWhiteSpace(asset.SourceUrl)
            || !Uri.TryCreate(asset.SourceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("This item has no direct media URL, so it can't be restored.");

        // Download to a temp file (preserving the extension so the store/MIME stay correct), then re-store
        // it — identical content lands back at the same content-addressed path.
        var ext = Path.GetExtension(uri.AbsolutePath);
        var tempDir = Path.Combine(Path.GetTempPath(), "hoard-restore");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ext);
        try
        {
            await using (var response = await RestoreHttp.GetStreamAsync(uri, ct).ConfigureAwait(false))
            await using (var file = File.Create(tempPath))
                await response.CopyToAsync(file, ct).ConfigureAwait(false);

            var blob = await _store.PutAsync(tempPath, ct).ConfigureAwait(false);
            asset.RelativePath = blob.RelativePath;
            asset.Sha256 = blob.Sha256;
            asset.Bytes = blob.Bytes;
            asset.DeletedAt = null;
            asset.DeletionNote = null;
            SyncLog.RecordAdd(db, asset);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToView(asset);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Every (board, pin) pair the library tracks — live or tombstoned — so a connector can rebuild
    /// its skip-archive from the DB rather than a separate, drift-prone list.</summary>
    private static async Task<IReadOnlyCollection<KnownSourceItem>> GetKnownItemsAsync(HoardDbContext db, CancellationToken ct)
    {
        var rows = await db.CollectionItems
            .Where(ci => ci.Asset.SourceId != null && ci.Collection.SourceBoardId != null)
            .Select(ci => new { Board = ci.Collection.SourceBoardId!, Source = ci.Asset.SourceId! })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new KnownSourceItem(r.Board, r.Source)).ToList();
    }

    private AssetView ToView(Asset a) =>
        new(a.Id, _store.GetAbsolutePath(a.RelativePath), a.Kind, a.Title, a.Description,
            a.SourceUrl, a.Width, a.Height, a.Sha256, a.DeletedAt is not null, a.DeletionNote);

    private static async Task<Asset?> FindExistingAssetAsync(
        HoardDbContext db, Dictionary<string, Asset> cache, string sha256, CancellationToken ct)
    {
        if (cache.TryGetValue(sha256, out var cached)) return cached;
        var existing = await db.Assets.FirstOrDefaultAsync(a => a.Sha256 == sha256, ct).ConfigureAwait(false);
        if (existing is not null) cache[sha256] = existing;
        return existing;
    }

    private Asset CreateAsset(HoardDbContext db, Dictionary<string, Asset> cache, StoredBlob blob, SourceMediaItem item)
    {
        var (kind, mime) = MediaTypes.FromPath(item.FilePath);
        var asset = new Asset
        {
            Sha256 = blob.Sha256,
            RelativePath = blob.RelativePath,
            Bytes = blob.Bytes,
            MimeType = mime,
            Kind = kind,
            Width = item.Width,
            Height = item.Height,
            SourceConnector = item.Connector,
            SourceId = item.SourceId,
            SourceUrl = item.SourceUrl,
            OriginalUrl = item.OriginalUrl,
            Title = item.Title,
            Description = item.Description,
            MetadataJson = item.RawJson,
            CreatedAt = item.CreatedAt,
            ImportedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(asset);
        // Log the add in the same SaveChanges as the asset, so the sync history can never drift from
        // what's actually in the library.
        SyncLog.RecordAdd(db, asset);
        cache[blob.Sha256] = asset;
        return asset;
    }

    private async Task<Collection?> GetOrCreateCollectionAsync(
        HoardDbContext db, Dictionary<string, Collection> cache, SourceMediaItem item, string connectorName, CancellationToken ct)
    {
        var key = item.BoardId ?? item.BoardName;
        if (key is null) return null; // a loose pin with no board — archived, just not foldered

        if (cache.TryGetValue(key, out var cached)) return cached;

        Collection? collection = null;
        if (item.BoardId is not null)
            collection = await db.Collections.FirstOrDefaultAsync(
                c => c.SourceConnector == connectorName && c.SourceBoardId == item.BoardId, ct).ConfigureAwait(false);
        collection ??= await db.Collections.FirstOrDefaultAsync(
            c => c.SourceConnector == connectorName && c.Name == (item.BoardName ?? key), ct).ConfigureAwait(false);

        if (collection is null)
        {
            collection = new Collection
            {
                Name = item.BoardName ?? key,
                SourceConnector = connectorName,
                SourceBoardId = item.BoardId,
                SourceUrl = item.BoardUrl,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Collections.Add(collection);
        }
        cache[key] = collection;
        return collection;
    }

    private static async Task LinkToCollectionAsync(
        HoardDbContext db, Collection collection, Asset asset, SourceMediaItem item, CancellationToken ct)
    {
        // Same-run duplicates resolve against the tracked graph; cross-run duplicates (both already
        // persisted) resolve against the DB so we never violate the (CollectionId, AssetId) index.
        var alreadyLinked = collection.Items.Any(ci => ReferenceEquals(ci.Asset, asset))
                            || asset.CollectionItems.Any(ci => ReferenceEquals(ci.Collection, collection));
        if (!alreadyLinked && collection.Id != 0 && asset.Id != 0)
            alreadyLinked = await db.CollectionItems
                .AnyAsync(ci => ci.CollectionId == collection.Id && ci.AssetId == asset.Id, ct).ConfigureAwait(false);
        if (alreadyLinked) return;

        var link = new CollectionItem
        {
            Collection = collection,
            Asset = asset,
            Note = item.Description,
            AddedAt = DateTimeOffset.UtcNow,
        };
        collection.Items.Add(link);
        asset.CollectionItems.Add(link);
    }

    private async Task AttachTagsAsync(
        HoardDbContext db, Dictionary<string, Tag> cache, Asset asset, SourceMediaItem item, CancellationToken ct)
    {
        foreach (var name in item.Tags)
        {
            if (!cache.TryGetValue(name, out var tag))
            {
                tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct).ConfigureAwait(false)
                      ?? new Tag { Name = name };
                if (tag.Id == 0) db.Tags.Add(tag);
                cache[name] = tag;
            }
            var linked = asset.AssetTags.Any(at => ReferenceEquals(at.Tag, tag));
            if (!linked && asset.Id != 0 && tag.Id != 0)
                linked = await db.AssetTags.AnyAsync(at => at.AssetId == asset.Id && at.TagId == tag.Id, ct).ConfigureAwait(false);
            if (!linked)
                asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
        }
    }
}
