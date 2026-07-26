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
        var assetsByIdentity = new Dictionary<string, Asset>();
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
        var effectiveOptions = options with { KnownItems = await GetKnownItemsAsync(db, _store, targetCollectionId, ct).ConfigureAwait(false) };

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
            // Identity is the PIN — (connector, SourceId) — falling back to content for a pinless item
            // (unparseable sidecar), so re-imports stay idempotent either way.
            var existing = await FindExistingAssetAsync(
                db, assetsByIdentity, item.Connector, item.SourceId, blob.Sha256, committed).ConfigureAwait(false);

            // Honour a tombstone: this pin was deliberately deleted, so don't resurrect it — and re-free
            // the blob we just re-stored, unless another LIVE row shares those bytes (rows aren't unique
            // per content, so a blob can have several referrers). The DB is the authority even if the
            // skip-archive missed it.
            if (existing is { DeletedAt: not null })
            {
                // A tombstone can never self-heal provenance through the refresh path (it returns here),
                // so stamp it from THIS crawl — without a board id the blacklist can't pre-skip the pin
                // and every future sync would re-download and re-free it.
                if (existing.SourceBoardId is null && item.BoardId is not null)
                {
                    existing.SourceBoardId = item.BoardId;
                    existing.SourceSectionId ??= item.SectionId;
                    await db.SaveChangesAsync(committed).ConfigureAwait(false);
                }
                var shared = await BlobReferences.IsSharedAsync(db, blob.RelativePath, existing.Id, committed)
                    .ConfigureAwait(false);
                if (!shared) await _store.DeleteAsync(blob.RelativePath, committed).ConfigureAwait(false);
                processed++;
                skippedDeleted++;
                progress?.Report(new IngestProgress(IngestPhase.Storing, processed, 0,
                    $"Skipped (deleted): {item.Title ?? item.SourceId ?? Path.GetFileName(item.FilePath)}"));
                return;
            }

            var isNew = existing is null;
            var asset = existing ?? CreateAsset(db, assetsByIdentity, blob, item);
            if (!isNew) RefreshAsset(db, asset, blob, item);

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
    /// link — matched by their first-class <see cref="Asset.SourceBoardId"/> (indexed provenance; never a
    /// sidecar re-parse). The set of "imported boards" is the crawl's sources <i>plus</i> the target's
    /// already-recorded sources, so a re-sync of a board that's now empty at the source (crawl emits nothing)
    /// still re-attaches its orphans. A sectioned orphan re-files into its section folder when that folder
    /// still exists (find-only — a re-attach doesn't mint folders the crawl didn't). Tombstoned (blacklisted)
    /// assets are excluded, and any pin the crawl actually handled is skipped (no double link). Returns how
    /// many were re-attached.
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
        var boardIds = sourceIdByBoard.Keys.ToList();
        var orphans = await db.Assets
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.SourceId != null && !a.CollectionItems.Any()
                        && a.SourceBoardId != null && boardIds.Contains(a.SourceBoardId))
            .ToListAsync(ct).ConfigureAwait(false);

        var reattached = 0;
        foreach (var orphan in orphans)
        {
            if (importedPins.Contains(orphan.SourceId!)) continue; // the crawl already re-listed this pin
            var source = sourceIdByBoard[orphan.SourceBoardId!];

            var linkTarget = targetCollection;
            if (orphan.SourceSectionId is { } sectionId)
            {
                var folder = await db.Collections.FirstOrDefaultAsync(
                    c => c.ParentId == targetCollection.Id && c.SourceSectionId == sectionId, ct).ConfigureAwait(false);
                if (folder is not null) linkTarget = folder;
            }

            var link = new CollectionItem
            {
                CollectionId = linkTarget.Id,
                AssetId = orphan.Id,
                CollectionSourceId = source.Id,
                AddedAt = DateTimeOffset.UtcNow,
            };
            db.CollectionItems.Add(link);
            _archive.RecordItemLinked(db, orphan, ArchiveLog.UidOf(linkTarget), source.Uid, link.Note, link.AddedAt);
            reattached++;
            progress?.Report(new IngestProgress(IngestPhase.Storing, 0, 0,
                $"Re-attached {orphan.Title ?? orphan.SourceId}", ToView(orphan), linkTarget.Id));
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

        // The re-download can yield different bytes; the row IS the pin, so its blob pointer just moves
        // (no unique-sha constraint to collide with — another pin may legitimately hold the same bytes).
        var oldSha = asset.Sha256; // the op keys on the old identity so other machines can follow the transition
        var blob = await ReDownloadAsync(asset, ct).ConfigureAwait(false);
        asset.RelativePath = blob.RelativePath;
        asset.Sha256 = blob.Sha256;
        asset.Bytes = blob.Bytes;
        asset.DeletedAt = null;
        asset.DeletionNote = null;
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
    /// rebuilt from the DB so the skip-archive can never drift. Each pin skips under its OWN
    /// <see cref="Asset.SourceBoardId"/> — first-class provenance, no join through CollectionSources, no
    /// over-claiming. It covers exactly:
    ///   • live pins already held anywhere in the target board's subtree (so an incremental re-import
    ///     skips what's there — but NOT pins merely held in some other board: those must still be linked
    ///     into the target, which is what lets a re-import/Sync repopulate a board), and
    ///   • every <b>tombstoned (blacklisted)</b> pin, globally and regardless of links (a deleted pin
    ///     never re-fetches, even after its board went away).
    /// A held live pin whose <b>blob is missing/torn on disk</b> is deliberately NOT emitted, so a Sync
    /// repairs lost files by re-downloading them (belt-and-braces: <see cref="ImportAsync"/> re-checks
    /// <c>DeletedAt</c> after download). A legacy row not yet carrying provenance can't pre-skip — its
    /// pin re-downloads once and the pin-keyed upsert stamps it (self-heal). A null target keeps the
    /// whole-project behaviour for the auto-folder path.
    /// </summary>
    private static async Task<IReadOnlyCollection<KnownSourceItem>> GetKnownItemsAsync(
        HoardDbContext db, IMediaStore store, int? targetCollectionId, CancellationToken ct)
    {
        // Presence + length come from ONE store walk, not a stat per pin — the store may live on a
        // network share where per-file round-trips cost milliseconds each.
        Dictionary<string, long>? blobLengths = null;
        bool HasIntactBlob(string relativePath, long bytes)
        {
            blobLengths ??= Directory.Exists(store.Root)
                ? Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories)
                    .ToDictionary(Path.GetFullPath, f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            return blobLengths.TryGetValue(Path.GetFullPath(store.GetAbsolutePath(relativePath)), out var length)
                   && length == bytes;
        }

        var held = db.CollectionItems.AsQueryable();
        if (targetCollectionId is int root)
        {
            var subtree = await CollectionTree.SubtreeIdsAsync(db, root, ct).ConfigureAwait(false);
            held = held.Where(ci => subtree.Contains(ci.CollectionId));
        }
        var rows = await held
            .Where(ci => ci.Asset.DeletedAt == null && ci.Asset.SourceId != null && ci.Asset.SourceBoardId != null)
            .Select(ci => new { Board = ci.Asset.SourceBoardId!, Source = ci.Asset.SourceId!, ci.Asset.RelativePath, ci.Asset.Bytes })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var known = new HashSet<KnownSourceItem>();
        foreach (var row in rows)
        {
            if (!HasIntactBlob(row.RelativePath, row.Bytes)) continue; // lost blob → let the crawl repair it
            known.Add(new KnownSourceItem(row.Board, row.Source));
        }

        var tombstoned = await db.Assets
            .Where(a => a.DeletedAt != null && a.SourceId != null && a.SourceBoardId != null)
            .Select(a => new { Board = a.SourceBoardId!, Source = a.SourceId! })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in tombstoned)
            known.Add(new KnownSourceItem(t.Board, t.Source));

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

    /// <summary>The one identity rule: an asset is its pin — (connector, SourceId) — and only a pinless
    /// item (unparseable sidecar) falls back to content identity. Ordered by Id so a legacy duplicate
    /// (the dedup era could record one pin twice across content revisions) resolves deterministically.</summary>
    private static string IdentityKey(string connector, string? sourceId, string sha256) =>
        ArchiveOpKeys.ForAsset(connector, sourceId, sha256);

    private static async Task<Asset?> FindExistingAssetAsync(
        HoardDbContext db, Dictionary<string, Asset> cache, string connector, string? sourceId, string sha256,
        CancellationToken ct)
    {
        var key = IdentityKey(connector, sourceId, sha256);
        if (cache.TryGetValue(key, out var cached)) return cached;
        // LIVE rows win over tombstoned duplicates (legacy dedup-era data can hold one pin twice): the
        // pin is live if ANY of its rows is, so the tombstone-skip must not fire off a stale duplicate.
        // The pinless sha fallback matches only pinless rows — capturing a pinned row that happens to
        // share the bytes would hand its identity to an anonymous item.
        var existing = sourceId is not null
            ? await db.Assets.Where(a => a.SourceConnector == connector && a.SourceId == sourceId)
                .OrderBy(a => a.DeletedAt != null).ThenBy(a => a.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : await db.Assets.Where(a => a.Sha256 == sha256 && a.SourceId == null)
                .OrderBy(a => a.DeletedAt != null).ThenBy(a => a.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null) cache[key] = existing;
        return existing;
    }

    /// <summary>
    /// Follow a re-crawled pin whose substance moved on at the source: new bytes re-point the row's blob
    /// (the old blob stays on disk — another row may share it, and the verifier reports true orphans),
    /// and changed semantic fields refresh in place. Any material change re-emits <c>asset.added</c>,
    /// whose replay is an upsert-by-pin, so every machine converges on the latest state (LWW by HLC).
    /// The raw sidecar alone never counts as a change — its drifting counters would otherwise emit an op
    /// per pin on every sync.
    /// </summary>
    private void RefreshAsset(HoardDbContext db, Asset asset, StoredBlob blob, SourceMediaItem item)
    {
        var changed = false;
        if (asset.Sha256 != blob.Sha256)
        {
            asset.Sha256 = blob.Sha256;
            asset.RelativePath = blob.RelativePath;
            asset.Bytes = blob.Bytes;
            var (kind, mime) = MediaTypes.FromPath(item.FilePath);
            asset.Kind = kind;
            asset.MimeType = mime;
            changed = true;
        }
        changed |= UpdateIfFresh(item.BoardId, asset.SourceBoardId, v => asset.SourceBoardId = v);
        if (asset.SourceSectionId != item.SectionId) // a section move is real either way, incl. → none
        {
            asset.SourceSectionId = item.SectionId;
            changed = true;
        }
        changed |= UpdateIfFresh(item.Title, asset.Title, v => asset.Title = v);
        changed |= UpdateIfFresh(item.Description, asset.Description, v => asset.Description = v);
        changed |= UpdateIfFresh(item.SourceUrl, asset.SourceUrl, v => asset.SourceUrl = v);
        changed |= UpdateIfFresh(item.OriginalUrl, asset.OriginalUrl, v => asset.OriginalUrl = v);
        if (item.Width is int w && item.Height is int h && (asset.Width != w || asset.Height != h))
        {
            asset.Width = w;
            asset.Height = h;
            changed = true;
        }

        if (!changed) return;
        asset.MetadataJson = item.RawJson ?? asset.MetadataJson; // ride along with a real change only
        _archive.RecordAssetAdded(db, asset, item.Tags);

        // A fresh non-null value replaces a different old one; a null (this crawl's sidecar was poorer)
        // never erases what we know.
        static bool UpdateIfFresh(string? fresh, string? current, Action<string> apply)
        {
            if (fresh is null || fresh == current) return false;
            apply(fresh);
            return true;
        }
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
            SourceBoardId = item.BoardId,
            SourceSectionId = item.SectionId,
            SourceUrl = item.SourceUrl,
            OriginalUrl = item.OriginalUrl,
            Title = item.Title,
            Description = item.Description,
            MetadataJson = item.RawJson,
            CreatedAt = item.CreatedAt,
            ImportedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(asset);
        // Log the add in the same SaveChanges as the asset, so the archive history can never drift from
        // what's actually in the library.
        _archive.RecordAssetAdded(db, asset, item.Tags);
        cache[IdentityKey(item.Connector, item.SourceId, blob.Sha256)] = asset;
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
        _archive.RecordItemLinked(db, asset, ArchiveLog.UidOf(collection),
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
        _archive.RecordAssetRetagged(db, asset, full);
    }
}
