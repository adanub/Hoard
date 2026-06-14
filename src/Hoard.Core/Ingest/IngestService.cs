using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
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

    public async Task<IngestResult> ImportAsync(
        string url, ConnectorOptions options, IProgress<IngestProgress>? progress, CancellationToken ct = default)
    {
        var connector = _connectors.FirstOrDefault(c => c.CanHandle(url))
            ?? throw new NotSupportedException($"No connector can handle '{url}'.");

        progress?.Report(new IngestProgress(IngestPhase.Starting, 0, 0, $"Starting {connector.Name} download…"));

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Local caches so repeated keys within a single import resolve before SaveChanges.
        var assetsBySha = new Dictionary<string, Asset>();
        var collections = new Dictionary<string, Collection>();
        var tags = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);

        int processed = 0, newAssets = 0, duplicates = 0;

        var downloadLog = new Progress<string>(line =>
            progress?.Report(new IngestProgress(IngestPhase.Downloading, processed, 0, line)));

        // Each item is ingested the moment the connector finishes downloading it, and the freshly
        // imported asset is reported so the UI can show it immediately (not after the whole batch).
        await connector.DownloadAsync(url, options, downloadLog, async (item, itemCt) =>
        {
            var blob = await _store.PutAsync(item.FilePath, itemCt).ConfigureAwait(false);
            var (asset, isNew) = await GetOrCreateAssetAsync(db, assetsBySha, blob, item, itemCt).ConfigureAwait(false);

            var collection = await GetOrCreateCollectionAsync(db, collections, item, connector.Name, itemCt).ConfigureAwait(false);
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

        progress?.Report(new IngestProgress(IngestPhase.Done, processed, processed,
            $"Imported {newAssets} new, {duplicates} already-had."));
        return new IngestResult(processed, newAssets, duplicates, collections.Count);
    }

    private AssetView ToView(Asset a) =>
        new(a.Id, _store.GetAbsolutePath(a.RelativePath), a.Kind, a.Title, a.Description,
            a.SourceUrl, a.Width, a.Height, a.Sha256);

    private async Task<(Asset asset, bool isNew)> GetOrCreateAssetAsync(
        HoardDbContext db, Dictionary<string, Asset> cache, StoredBlob blob, SourceMediaItem item, CancellationToken ct)
    {
        if (cache.TryGetValue(blob.Sha256, out var cached))
            return (cached, false);

        var existing = await db.Assets.FirstOrDefaultAsync(a => a.Sha256 == blob.Sha256, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            cache[blob.Sha256] = existing;
            return (existing, false);
        }

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
        cache[blob.Sha256] = asset;
        return (asset, true);
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
