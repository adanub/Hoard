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
    private readonly ArchiveLog _archive;

    public IngestService(
        IDbContextFactory<HoardDbContext> dbFactory,
        IMediaStore store,
        IEnumerable<ISourceConnector> connectors,
        ILogger<IngestService>? logger = null,
        ArchiveLog? archive = null)
    {
        _dbFactory = dbFactory;
        _store = store;
        _connectors = connectors.ToList();
        _logger = logger ?? NullLogger<IngestService>.Instance;
        // The null fallback (tests/headless) mints its own device id, so two service instances writing
        // the same DB can never collide on the unique (DeviceId, Seq) index.
        _archive = archive ?? new ArchiveLog(ArchiveLog.NewUid());
    }

    /// <summary>
    /// Create an empty local board (collection) up front — so an import has somewhere to land immediately and
    /// its card can show progress from the first pin — and return its id. The connector is resolved from the
    /// <paramref name="url"/> the board will be filled from. Pass <paramref name="parentId"/> to create a
    /// <b>child folder</b> instead of a top-level board (a Pinterest section or a locally-created sub-folder);
    /// <paramref name="sectionId"/> records its source section id so a re-import re-finds it.
    /// </summary>
    public async Task<int> CreateBoardAsync(
        string name, string url, int? parentId = null, string? sectionId = null, CancellationToken ct = default)
    {
        var connectorName = _connectors.FirstOrDefault(c => c.CanHandle(url))?.Name ?? "";
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var collection = new Collection
        {
            Name = name,
            SourceConnector = connectorName,
            ParentId = parentId,
            SourceSectionId = sectionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Collections.Add(collection);

        string? parentUid = null;
        if (parentId is int pid)
        {
            var parent = await db.Collections.FirstOrDefaultAsync(c => c.Id == pid, ct).ConfigureAwait(false);
            if (parent is not null) parentUid = ArchiveLog.UidOf(parent);
        }
        _archive.RecordCollectionCreated(db, collection, parentUid);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
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
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);

        var targetCollection = targetCollectionId is int tid
            // Include the existing sources so EnsureSourceAsync's in-graph check sees them (no per-source query).
            ? await db.Collections.Include(c => c.Sources).FirstOrDefaultAsync(c => c.Id == tid, ct).ConfigureAwait(false)
            : null;

        // Local caches so repeated keys within a single import resolve before SaveChanges.
        var assetsBySha = new Dictionary<string, Asset>();
        var collections = new Dictionary<string, Collection>();
        var tags = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        // The resolved CollectionSource per (connector, board id) this run — keyed like the uniqueness key —
        // so each source is resolved once and every link can be attributed to it.
        var sourceByBoard = new Dictionary<(string Connector, string BoardId), CollectionSource>();
        // The child folder per (parent board, section id) this run, so sectioned pins file into one matching
        // folder rather than creating a duplicate per pin. Keyed by the parent Collection *reference* (stable even
        // while its Id is 0 before its first save — the auto-folder path) rather than parent.Id.
        var sectionFolders = new Dictionary<(Collection Parent, string SectionId), Collection>();
        // Every pin the crawl emitted (even tombstone-skipped ones), so the post-crawl orphan re-attach never
        // double-handles a pin that was actually re-listed.
        var importedPins = new HashSet<string>();

        int processed = 0, newAssets = 0, duplicates = 0, skippedDeleted = 0;

        var downloadLog = new Progress<string>(line =>
            progress?.Report(new IngestProgress(IngestPhase.Downloading, processed, 0, line)));

        // Hand the connector what to pre-skip, rebuilt from the DB so its archive never drifts. For a targeted
        // import this is what's already in THAT board (+ blacklisted tombstones) — NOT pins merely held in some
        // other board — so a re-import/sync re-links pins the target is missing instead of skipping them.
        var effectiveOptions = options with { KnownItems = await GetKnownItemsAsync(db, targetCollectionId, ct).ConfigureAwait(false) };

        // Each item is ingested the moment the connector finishes downloading it, and the freshly
        // imported asset is reported so the UI can show it immediately (not after the whole batch).
        await connector.DownloadAsync(url, effectiveOptions, downloadLog, async (item, itemCt) =>
        {
            // Each item is ATOMIC once its blob lands: cancellation (backing out of a board mid-sync, closing the
            // Library mid-import) is honoured between items — up to and including PutAsync — but never inside the
            // blob→row window. A cancel between PutAsync and SaveChangesAsync would strand an orphaned blob with no
            // Asset row; worse, one between PutAsync and the tombstone-compensating DeleteAsync below would silently
            // RESURRECT a deliberately-freed tombstone blob, and nothing ever revisits those (re-syncs pre-skip
            // tombstoned pins). The remaining per-item work is a handful of local DB calls — cancellation latency
            // stays milliseconds.
            itemCt.ThrowIfCancellationRequested();
            var committed = CancellationToken.None;

            if (item.SourceId is not null) importedPins.Add(item.SourceId);
            var blob = await _store.PutAsync(item.FilePath, itemCt).ConfigureAwait(false);
            var existing = await FindExistingAssetAsync(db, assetsBySha, blob.Sha256, committed).ConfigureAwait(false);

            // Honour a tombstone: this content was deliberately deleted, so don't resurrect it (and drop
            // the blob we just re-stored). The DB is the authority even if the skip-archive missed it.
            if (existing is { DeletedAt: not null })
            {
                await _store.DeleteAsync(blob.RelativePath, committed).ConfigureAwait(false);
                processed++;
                skippedDeleted++;
                progress?.Report(new IngestProgress(IngestPhase.Storing, processed, 0,
                    $"Skipped (deleted): {item.Title ?? item.SourceId ?? Path.GetFileName(item.FilePath)}"));
                return;
            }

            var isNew = existing is null;
            var asset = existing ?? CreateAsset(db, assetsBySha, blob, item);

            // Import into the one board the user chose/created (a merge can add several source boards), or
            // auto-folder by the pin's own source board.
            var collection = targetCollection
                ?? await GetOrCreateCollectionAsync(db, collections, item, connector.Name, committed).ConfigureAwait(false);

            // Resolve (and record) which source board this pin came from, so the link can be attributed to it
            // and a source can later be un-merged together with its own images.
            CollectionSource? source = null;
            if (collection is not null && item.BoardId is not null)
            {
                var key = (connector.Name, item.BoardId);
                if (!sourceByBoard.TryGetValue(key, out source))
                {
                    source = await GetOrAddSourceAsync(db, collection, connector.Name, item, item.BoardUrl ?? url, committed).ConfigureAwait(false);
                    sourceByBoard[key] = source;
                }
                // Keep the denormalised primary pointer seeded from the first source seen.
                if (collection.SourceBoardId is null)
                {
                    collection.SourceBoardId = item.BoardId;
                    collection.SourceUrl = item.BoardUrl;
                }
            }

            // A pin inside a Pinterest section files into a matching child folder (a child Collection of the
            // board); a sectionless pin lands on the board's main grid. The link keeps the board's source
            // attribution either way (the pin still came from that source board). The folder is created + the
            // link committed within THIS item's SaveChanges (below), so an interrupted import never leaves a
            // downloaded pin un-filed.
            var linkTarget = collection;
            if (collection is not null)
            {
                linkTarget = item.SectionId is { Length: > 0 } sectionId
                    ? await GetOrCreateSectionFolderAsync(db, sectionFolders, collection, sectionId, item, committed).ConfigureAwait(false)
                    : collection;
                await LinkToCollectionAsync(db, linkTarget, asset, item, source, committed).ConfigureAwait(false);
            }

            await AttachTagsAsync(db, tags, asset, item, isNew, committed).ConfigureAwait(false);

            // Persist per item so the asset gets an Id and is immediately queryable/displayable.
            await db.SaveChangesAsync(committed).ConfigureAwait(false);

            processed++;
            if (isNew) newAssets++; else duplicates++;

            // Surface genuinely new images to the grid, tagged with the collection they actually landed in (the
            // board, or a section folder) so the live view files them in the right place rather than dumping all
            // of them on the board.
            var view = isNew ? ToView(asset) : null;
            progress?.Report(new IngestProgress(IngestPhase.Storing, processed, 0,
                $"Imported {processed} — {item.Title ?? item.SourceId ?? Path.GetFileName(item.FilePath)}",
                view, linkTarget?.Id));
        }, ct).ConfigureAwait(false);

        var reattached = await ReattachOrphansAsync(db, targetCollection, sourceByBoard, importedPins, connector.Name, progress, ct).ConfigureAwait(false);

        // One segment flush for the whole import (not per item): everything committed above re-lands
        // from the authoritative table even if this call never runs (crash/cancel).
        await _archive.FlushSegmentAsync(db, CancellationToken.None).ConfigureAwait(false);

        var skippedNote = skippedDeleted > 0 ? $", {skippedDeleted} skipped (deleted)" : "";
        var reattachedNote = reattached > 0 ? $", {reattached} re-attached" : "";
        progress?.Report(new IngestProgress(IngestPhase.Done, processed, processed,
            $"Imported {newAssets} new, {duplicates} already-had{skippedNote}{reattachedNote}."));
        var touched = targetCollection is not null ? 1 : collections.Count;
        return new IngestResult(processed, newAssets, duplicates, touched);
    }

    /// <summary>
    /// After a targeted import, re-attach <b>orphaned live</b> assets that belong to a board we just imported but
    /// weren't re-listed by the crawl (e.g. a restored image whose pin was removed from the source board, so
    /// gallery-dl never sees it again). Their content is already in the library — they only lost their board
    /// link — so we link them back into the target by the board id in their stored sidecar. The set of "imported
    /// boards" is the crawl's sources <i>plus</i> the target's already-recorded sources, so a re-sync of a board
    /// that's now empty at the source (crawl emits nothing) still re-attaches its orphans. Tombstoned
    /// (blacklisted) assets are excluded, and any pin the crawl actually handled is skipped (no double link).
    /// Returns how many were re-attached.
    /// </summary>
    private async Task<int> ReattachOrphansAsync(
        HoardDbContext db, Collection? targetCollection,
        Dictionary<(string Connector, string BoardId), CollectionSource> sourceByBoard,
        HashSet<string> importedPins, string connectorName, IProgress<IngestProgress>? progress, CancellationToken ct)
    {
        if (targetCollection is null) return 0;

        // The board id → CollectionSource (id + uid) the orphan should be attributed to: this run's crawl
        // sources, plus the target's existing sources (so re-sync of a now-empty board still finds its board id).
        var sourceIdByBoard = new Dictionary<string, (int Id, string? Uid)>();
        foreach (var ((connector, boardId), source) in sourceByBoard)
            if (connector == connectorName) sourceIdByBoard[boardId] = (source.Id, ArchiveLog.UidOf(source));
        foreach (var s in await db.CollectionSources
                     .Where(s => s.CollectionId == targetCollection.Id && s.SourceConnector == connectorName && s.SourceBoardId != null)
                     .Select(s => new { s.Id, s.SourceBoardId, s.Uid })
                     .ToListAsync(ct).ConfigureAwait(false))
            sourceIdByBoard.TryAdd(s.SourceBoardId!, (s.Id, s.Uid));
        if (sourceIdByBoard.Count == 0) return 0;

        // Un-tracked read (we only insert links, never modify the orphan rows).
        var orphans = await db.Assets
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.SourceId != null && !a.CollectionItems.Any())
            .ToListAsync(ct).ConfigureAwait(false);

        var reattached = 0;
        foreach (var orphan in orphans)
        {
            if (importedPins.Contains(orphan.SourceId!)) continue; // the crawl already re-listed this pin
            var boardId = SidecarBoardId.From(orphan.MetadataJson);
            if (boardId is null || !sourceIdByBoard.TryGetValue(boardId, out var source)) continue;

            var link = new CollectionItem
            {
                CollectionId = targetCollection.Id,
                AssetId = orphan.Id,
                CollectionSourceId = source.Id,
                AddedAt = DateTimeOffset.UtcNow,
            };
            db.CollectionItems.Add(link);
            _archive.RecordItemLinked(db, orphan.Sha256, ArchiveLog.UidOf(targetCollection), source.Uid, link.Note, link.AddedAt);
            reattached++;
            progress?.Report(new IngestProgress(IngestPhase.Storing, 0, 0,
                $"Re-attached {orphan.Title ?? orphan.SourceId}", ToView(orphan), targetCollection.Id));
        }
        if (reattached > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return reattached;
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
        await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct).ConfigureAwait(false);
        if (asset is null || asset.DeletedAt is null) return null;

        var oldSha = asset.Sha256; // the re-download can yield different bytes; the op keys on the old identity
        var blob = await ReDownloadAsync(asset, ct).ConfigureAwait(false);
        asset.RelativePath = blob.RelativePath;
        asset.Sha256 = blob.Sha256;
        asset.Bytes = blob.Bytes;
        asset.DeletedAt = null;
        asset.DeletionNote = null;
        SyncLog.RecordAdd(db, asset); // a restore is a genuine re-add to the library
        _archive.RecordAssetRestored(db, oldSha, asset);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        return ToView(asset);
    }

    /// <summary>
    /// Re-fetch a <b>live</b> asset whose blob has gone missing from the store (deleted/moved/corrupted outside
    /// the app) from its saved media URL, re-storing it in place. A no-op (returns the view) if the blob is
    /// already present; null if the asset is missing or is a tombstone (use <see cref="RestoreAsync"/> for those).
    /// Throws if there's no usable media URL. Not a delete/add cycle, so it writes no sync op.
    /// </summary>
    public async Task<AssetView?> RefetchAsync(int assetId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct).ConfigureAwait(false);
        if (asset is null || asset.DeletedAt is not null) return null;
        if (File.Exists(_store.GetAbsolutePath(asset.RelativePath))) return ToView(asset); // already present

        var oldSha = asset.Sha256;
        var blob = await ReDownloadAsync(asset, ct).ConfigureAwait(false);
        asset.RelativePath = blob.RelativePath;
        asset.Sha256 = blob.Sha256;
        asset.Bytes = blob.Bytes;
        // Same bytes back = pure repair of local state, no op. Different bytes = the asset's content
        // identity changed, which every other machine must follow.
        if (asset.Sha256 != oldSha)
        {
            await _archive.EnsureReadyAsync(db, ct).ConfigureAwait(false);
            _archive.RecordAssetRefetched(db, oldSha, asset);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _archive.FlushSegmentAsync(db, ct).ConfigureAwait(false);
        return ToView(asset);
    }

    /// <summary>
    /// Download an asset's media from its saved public URL into the content-addressed store and return the
    /// stored blob. Shared by restore (tombstoned) and re-fetch (live but missing). Throws if the asset has no
    /// usable http(s) media URL.
    /// </summary>
    private async Task<StoredBlob> ReDownloadAsync(Asset asset, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(asset.SourceUrl)
            || !Uri.TryCreate(asset.SourceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("This item has no direct media URL, so it can't be re-downloaded.");

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
            return await _store.PutAsync(tempPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>
    /// The (board, pin) pairs the connector should pre-skip when filling <paramref name="targetCollectionId"/>,
    /// rebuilt from the DB so the skip-archive can never drift. It deliberately covers only:
    ///   • pins ALREADY IN the target board (so an incremental re-import skips what's there), and
    ///   • every <b>tombstoned (blacklisted)</b> pin, globally (so deleted content is never re-fetched).
    /// It does <b>not</b> skip a live pin just because it sits in some <i>other</i> board — that pin must still be
    /// linked into the target, so it's left for download → dedup-by-hash → link (this is what lets a re-import or
    /// Sync repopulate a board that's missing items it holds elsewhere). The blacklist correctness is also
    /// belt-and-braces: <see cref="ImportAsync"/> re-checks <c>DeletedAt</c> after download. A null target keeps
    /// the legacy whole-project behaviour for the auto-folder path. Flat join (not a SelectMany over the nav,
    /// which SQLite can't APPLY); over-claiming a pin under a sibling source board is safe (only skips content we
    /// already hold for that board).
    /// </summary>
    private static async Task<IReadOnlyCollection<KnownSourceItem>> GetKnownItemsAsync(
        HoardDbContext db, int? targetCollectionId, CancellationToken ct)
    {
        var rows = await (
            from ci in db.CollectionItems
            where ci.Asset.SourceId != null
                  && (targetCollectionId == null || ci.CollectionId == targetCollectionId || ci.Asset.DeletedAt != null)
            join s in db.CollectionSources on ci.CollectionId equals s.CollectionId
            where s.SourceBoardId != null
            select new { Board = s.SourceBoardId!, Source = ci.Asset.SourceId! })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var known = new HashSet<KnownSourceItem>(rows.Select(r => new KnownSourceItem(r.Board, r.Source)));

        // Section pins sit in child folders whose CollectionId carries no CollectionSource of its own (sources
        // live on the root board), so the join above misses them and a re-sync would re-download every sectioned
        // pin. For a targeted import, pair each held pin in a descendant folder with each of the board's source
        // board ids — over-claiming a held pin is safe (it only ever skips content we already have).
        if (targetCollectionId is int root)
        {
            var descendants = (await CollectionTree.SubtreeIdsAsync(db, root, ct).ConfigureAwait(false))
                .Where(id => id != root).ToList();
            if (descendants.Count > 0)
            {
                var rootBoards = await db.CollectionSources
                    .Where(s => s.CollectionId == root && s.SourceBoardId != null)
                    .Select(s => s.SourceBoardId!)
                    .Distinct().ToListAsync(ct).ConfigureAwait(false);
                if (rootBoards.Count > 0)
                {
                    var sectionPins = await db.CollectionItems
                        .Where(ci => descendants.Contains(ci.CollectionId) && ci.Asset.SourceId != null)
                        .Select(ci => ci.Asset.SourceId!)
                        .Distinct().ToListAsync(ct).ConfigureAwait(false);
                    foreach (var board in rootBoards)
                        foreach (var pin in sectionPins)
                            known.Add(new KnownSourceItem(board, pin));
                }
            }
        }

        return known.ToList();
    }

    /// <summary>
    /// Find or create the child folder for a pin's source <i>section</i> (a child <see cref="Collection"/> of the
    /// board, matched by parent + section id), so sectioned pins file into it instead of the board's main grid.
    /// Cached per run; matches an existing folder on a re-import via <see cref="Collection.SourceSectionId"/>.
    /// </summary>
    private async Task<Collection> GetOrCreateSectionFolderAsync(
        HoardDbContext db, Dictionary<(Collection Parent, string SectionId), Collection> cache,
        Collection parent, string sectionId, SourceMediaItem item, CancellationToken ct)
    {
        var key = (parent, sectionId);
        if (cache.TryGetValue(key, out var folder)) return folder;

        // A saved parent (the usual targeted-import case) may already have this folder from a prior import.
        if (parent.Id != 0)
            folder = await db.Collections.FirstOrDefaultAsync(
                c => c.ParentId == parent.Id && c.SourceSectionId == sectionId, ct).ConfigureAwait(false);

        if (folder is null)
        {
            folder = new Collection
            {
                Name = string.IsNullOrWhiteSpace(item.SectionName) ? "Section" : item.SectionName!.Trim(),
                SourceConnector = parent.SourceConnector,
                Parent = parent, // wires ParentId on save whether or not the parent is persisted yet
                SourceSectionId = sectionId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Collections.Add(folder);
            _archive.RecordCollectionCreated(db, folder, ArchiveLog.UidOf(parent));
        }
        cache[key] = folder;
        return folder;
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
        _archive.RecordAssetAdded(db, asset, item.Tags);
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
            _archive.RecordCollectionCreated(db, collection, parentUid: null);
        }
        cache[key] = collection;
        return collection;
    }

    /// <summary>
    /// Resolve the board's record of this source (the Pinterest board it's being filled from), creating it on
    /// first sight. Idempotent on (collection, connector, source board id); returns the source so each imported
    /// link can be attributed to it.
    /// </summary>
    private async Task<CollectionSource> GetOrAddSourceAsync(
        HoardDbContext db, Collection collection, string connectorName, SourceMediaItem item, string fallbackUrl, CancellationToken ct)
    {
        var boardId = item.BoardId;
        var existing = collection.Sources.FirstOrDefault(s => s.SourceConnector == connectorName && s.SourceBoardId == boardId);
        if (existing is null && collection.Id != 0)
        {
            existing = await db.CollectionSources.FirstOrDefaultAsync(
                s => s.CollectionId == collection.Id && s.SourceConnector == connectorName && s.SourceBoardId == boardId, ct).ConfigureAwait(false);
            // Track it on the nav so the in-graph check finds it next time (no repeat query this run).
            if (existing is not null && !collection.Sources.Contains(existing)) collection.Sources.Add(existing);
        }
        if (existing is not null)
        {
            // A source backfilled (v3) without a URL becomes syncable once a real import supplies one —
            // a real mutation, so it's op'd (the index must stay derivable from the archive alone).
            if (string.IsNullOrEmpty(existing.SourceUrl) && !string.IsNullOrWhiteSpace(item.BoardUrl))
            {
                existing.SourceUrl = item.BoardUrl;
                _archive.RecordSourceUpdated(db, existing);
            }
            return existing;
        }

        var source = new CollectionSource
        {
            Collection = collection,
            SourceConnector = connectorName,
            SourceBoardId = boardId,
            SourceUrl = item.BoardUrl ?? fallbackUrl,
            Name = item.BoardName,
            AddedAt = DateTimeOffset.UtcNow,
        };
        collection.Sources.Add(source);
        db.CollectionSources.Add(source);
        _archive.RecordSourceAttached(db, source, collection);
        return source;
    }

    private async Task LinkToCollectionAsync(
        HoardDbContext db, Collection collection, Asset asset, SourceMediaItem item, CollectionSource? source, CancellationToken ct)
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
            CollectionSource = source, // which merged source this pin came from (null for a loose/board-less pin)
            Note = item.Description,
            AddedAt = DateTimeOffset.UtcNow,
        };
        collection.Items.Add(link);
        asset.CollectionItems.Add(link);
        _archive.RecordItemLinked(db, asset.Sha256, ArchiveLog.UidOf(collection),
            source is null ? null : ArchiveLog.UidOf(source), link.Note, link.AddedAt);
    }

    private async Task AttachTagsAsync(
        HoardDbContext db, Dictionary<string, Tag> cache, Asset asset, SourceMediaItem item, bool isNew, CancellationToken ct)
    {
        var attached = 0;
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
            {
                asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
                attached++;
            }
        }

        // A new asset's tags ride in its asset.added op; tags landing on an already-held asset (a
        // re-import from another board) are a mutation of their own and must be op'd — the payload is the
        // FULL resulting set (saved rows + this run's in-graph adds), applied as a replacement.
        if (isNew || attached == 0) return;
        var saved = asset.Id == 0
            ? []
            : await db.AssetTags.Where(at => at.AssetId == asset.Id)
                .Select(at => at.Tag.Name).ToListAsync(ct).ConfigureAwait(false);
        var full = saved
            .Concat(asset.AssetTags.Where(at => at.Tag is not null).Select(at => at.Tag.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        _archive.RecordAssetRetagged(db, asset.Sha256, full);
    }
}
