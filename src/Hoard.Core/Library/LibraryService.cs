using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

public sealed record AssetView(
    int Id, string AbsolutePath, MediaKind Kind, string? Title, string? Description,
    string? SourceUrl, int? Width, int? Height, string Sha256,
    bool IsDeleted = false, string? DeletionNote = null);

/// <summary>Full metadata for one asset, shown in the detail panel.</summary>
public sealed record AssetDetail(
    int Id, string AbsolutePath, MediaKind Kind, string? MimeType,
    string? Title, string? Description, string? SourceUrl, string? OriginalUrl, string? SourceId,
    int? Width, int? Height, long Bytes, DateTimeOffset? CreatedAt, DateTimeOffset ImportedAt,
    IReadOnlyList<string> Boards, bool IsDeleted = false, string? DeletionNote = null);

public sealed record CollectionView(int Id, string Name, int ItemCount, long SizeBytes);

/// <summary>One board's incremental-sync plan: the crawl targets for it, and the name to show while it runs.</summary>
public sealed record BoardSyncPlan(int CollectionId, string Name, IReadOnlyList<string> Targets);

/// <summary>One Pinterest source board merged into a local board (a row in the board Edit popup's source list).
/// <paramref name="ImageCount"/> is the board's live images attributed to this source (what un-merging with its
/// images would remove); 0 for links made before per-pin provenance.</summary>
public sealed record BoardSource(int Id, string? SourceBoardId, string SourceUrl, string? Name, int ImageCount);

/// <summary>Detail for the board Edit popup: per-kind counts, total size, created date, and merged sources.</summary>
public sealed record BoardDetail(
    int Images, int Gifs, int Videos, long SizeBytes, DateTimeOffset CreatedAt,
    IReadOnlyList<BoardSource> Sources);

/// <summary>Read-side queries for the UI. Kept separate from <c>IngestService</c> (writes).</summary>
public sealed class LibraryService
{
    private readonly IDbContextFactory<HoardDbContext> _dbFactory;
    private readonly IMediaStore _store;

    public LibraryService(IDbContextFactory<HoardDbContext> dbFactory, IMediaStore store)
    {
        _dbFactory = dbFactory;
        _store = store;
    }

    /// <summary>The project's <b>top-level</b> boards for the Library grid. Child folders (Pinterest sections /
    /// locally-created sub-folders, which have a <see cref="Collection.ParentId"/>) are excluded — they show on
    /// their parent's Board screen, not here. Each card's count/size is its <b>whole subtree</b> (its sections /
    /// sub-folders count toward it), so the board shows the total it actually holds.</summary>
    public async Task<IReadOnlyList<CollectionView>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (totals, childIds) = await LoadSubtreeRollupAsync(db, scope: null, ct); // every board → whole project
        var roots = await db.Collections
            .Where(c => c.ParentId == null)
            .Select(c => new { c.Id, Name = c.DisplayName ?? c.Name })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        return roots.Select(c =>
        {
            var (count, size) = Subtree(c.Id, totals, childIds);
            return new CollectionView(c.Id, c.Name, count, size);
        }).ToList();
    }

    /// <summary>The child folders of a board (its Pinterest sections + any locally-created sub-folders), for the
    /// folder-card row on the Board screen. Same shape as <see cref="GetCollectionsAsync"/> but scoped to one
    /// parent; each folder's count/size also rolls up its own sub-folders.</summary>
    public async Task<IReadOnlyList<CollectionView>> GetChildBoardsAsync(int parentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Only the parent's subtree is shown, so scope the rollup to it instead of aggregating the whole project
        // (this runs on every board open / resume / live-import folder refresh).
        var scope = await CollectionTree.SubtreeIdsAsync(db, parentId, ct);
        var (totals, childIds) = await LoadSubtreeRollupAsync(db, scope, ct);
        var children = await db.Collections
            .Where(c => c.ParentId == parentId)
            .Select(c => new { c.Id, Name = c.DisplayName ?? c.Name })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        return children.Select(c =>
        {
            var (count, size) = Subtree(c.Id, totals, childIds);
            return new CollectionView(c.Id, c.Name, count, size);
        }).ToList();
    }

    /// <summary>Load the per-collection live (non-tombstoned) image count + size and the parent→child edges, so a
    /// board's subtree total can be rolled up in memory. <paramref name="scope"/> null = the whole project (all
    /// top-level boards need it); otherwise restrict both queries to that set of collection ids (one board's
    /// subtree). A pin is linked to exactly one collection of a subtree — import links it once and a move
    /// re-points (never adds) the link — so summing per-collection counts never double-counts.</summary>
    private static async Task<(Dictionary<int, (int Count, long Size)> Totals, ILookup<int, int> ChildIds)>
        LoadSubtreeRollupAsync(HoardDbContext db, IReadOnlyCollection<int>? scope, CancellationToken ct)
    {
        var countsQuery = db.CollectionItems.Where(ci => ci.Asset.DeletedAt == null);
        if (scope is not null) countsQuery = countsQuery.Where(ci => scope.Contains(ci.CollectionId));
        var perCollection = await countsQuery
            .GroupBy(ci => ci.CollectionId)
            .Select(g => new { CollectionId = g.Key, Count = g.Count(), Size = g.Sum(ci => (long?)ci.Asset.Bytes) ?? 0L })
            .ToListAsync(ct);
        var totals = perCollection.ToDictionary(r => r.CollectionId, r => (r.Count, r.Size));

        var edgesQuery = db.Collections.Where(c => c.ParentId != null);
        if (scope is not null) edgesQuery = edgesQuery.Where(c => scope.Contains(c.ParentId!.Value));
        var edges = await edgesQuery
            .Select(c => new { Parent = c.ParentId!.Value, Child = c.Id })
            .ToListAsync(ct);
        return (totals, edges.ToLookup(e => e.Parent, e => e.Child));
    }

    /// <summary>Total live image count + size across a collection and every descendant. Iterative (arbitrary
    /// depth) with a cycle guard.</summary>
    private static (int Count, long Size) Subtree(
        int rootId, Dictionary<int, (int Count, long Size)> totals, ILookup<int, int> childIds)
    {
        var count = 0;
        long size = 0;
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            if (totals.TryGetValue(id, out var t)) { count += t.Count; size += t.Size; }
            foreach (var child in childIds[id]) stack.Push(child);
        }
        return (count, size);
    }

    /// <summary>
    /// The distinct source URLs a board can be re-fetched ("synced") from — its <see cref="Domain.CollectionSource"/>
    /// rows that carry a usable URL. Empty for a local board, the "All images" view, or sources with no URL.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetBoardSourceUrlsAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CollectionSources
            .Where(s => s.CollectionId == collectionId && s.SourceUrl != "")
            .OrderBy(s => s.Id)
            .Select(s => s.SourceUrl)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Every URL an <b>incremental</b> sync of this board has to crawl: each source board, then each
    /// <i>section</i> of it the board already holds pins from, as its own target.
    /// <para>Sections need their own targets because a connector that appends them after all of a board's own
    /// pins puts them beyond where an early stop lands — crawl only the board URL and a delta would silently
    /// stop syncing every folder. Which sections exist is read from the pins' own indexed provenance
    /// (<see cref="Domain.Asset.SourceBoardId"/> + <see cref="Domain.Asset.SourceSectionId"/>), never a
    /// sidecar re-parse, and each is attributed to the source board it actually came from — so a board
    /// merging several sources asks each source only for its own sections.</para>
    /// <para>This is by construction the set of sections seen <i>so far</i>: a section added at the source
    /// since the last full crawl exists neither as a folder nor on a pin here, so it gets no target.
    /// Discovering those is exactly what <see cref="Ingest.ImportMode.Full"/> is for.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSyncTargetsAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildSyncTargetsAsync(db, collectionId, ct);
    }

    /// <summary>
    /// The sync plan for the <b>whole project</b>: every top-level board that has something to crawl, with
    /// its targets, so a "sync everything" run knows its whole shape up front — and a board with no URL'd
    /// source (a purely local one) simply isn't in the list rather than being a no-op run.
    /// <para>It still costs a handful of queries per board (sources, subtree, sections); what it saves is a
    /// <see cref="HoardDbContext"/> per board, not the round trips. That's fine against a local SQLite index
    /// at project scale — if a project ever holds boards in the hundreds, batch the per-board queries before
    /// reaching for anything cleverer.</para>
    /// </summary>
    public async Task<IReadOnlyList<BoardSyncPlan>> GetProjectSyncPlanAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var boards = await db.Collections
            .Where(c => c.ParentId == null && c.Sources.Any(s => s.SourceUrl != ""))
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, Name = c.DisplayName ?? c.Name })
            .ToListAsync(ct);

        var plans = new List<BoardSyncPlan>();
        foreach (var board in boards)
        {
            ct.ThrowIfCancellationRequested();
            var targets = await BuildSyncTargetsAsync(db, board.Id, ct);
            if (targets.Count > 0) plans.Add(new BoardSyncPlan(board.Id, board.Name, targets));
        }
        return plans;
    }

    private static async Task<IReadOnlyList<string>> BuildSyncTargetsAsync(
        HoardDbContext db, int collectionId, CancellationToken ct)
    {
        var sources = await db.CollectionSources
            .Where(s => s.CollectionId == collectionId && s.SourceUrl != "")
            .OrderBy(s => s.Id)
            .Select(s => new { s.SourceUrl, s.SourceConnector, s.SourceBoardId })
            .ToListAsync(ct);
        if (sources.Count == 0) return [];

        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, collectionId, ct);
        var sections = (await db.CollectionItems
                .Where(ci => subtreeIds.Contains(ci.CollectionId)
                             && ci.Asset.DeletedAt == null
                             && ci.Asset.SourceBoardId != null
                             && ci.Asset.SourceSectionId != null)
                .Select(ci => new { Board = ci.Asset.SourceBoardId!, Section = ci.Asset.SourceSectionId! })
                .Distinct()
                .ToListAsync(ct))
            .Select(s => (s.Board, s.Section))
            .ToHashSet();

        // The folders themselves are the other half of the answer: one whose pins predate provenance, or
        // whose pins are all deleted, is still a folder that can gain new items. A folder records only its
        // section id, not which source board it came from — so this only applies where that's unambiguous.
        // "Unambiguous" counts ALL of the board's sources, not just the crawlable ones: a second source with
        // no URL still contributes sections, and attributing those to the one URL'd board would build targets
        // that 404 on every sync.
        var totalSources = await db.CollectionSources.CountAsync(s => s.CollectionId == collectionId, ct);
        if (totalSources == 1 && sources.Count == 1 && sources[0].SourceBoardId is { } onlyBoard)
        {
            foreach (var sectionId in await db.Collections
                         .Where(c => subtreeIds.Contains(c.Id) && c.SourceSectionId != null)
                         .Select(c => c.SourceSectionId!)
                         .Distinct()
                         .ToListAsync(ct))
                sections.Add((onlyBoard, sectionId));
        }

        // Distinct, order-preserving: two sources sharing a URL (or a section reachable from both) is one
        // target, and re-crawling it twice in a run would only re-walk the same pages.
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url)) targets.Add(url);
        }

        foreach (var source in sources)
        {
            Add(source.SourceUrl);
            // Sub-collection URLs are connector-specific shapes; a second connector adds its own case here.
            if (source.SourceConnector != PinterestSidecarParser.ConnectorName || source.SourceBoardId is null)
                continue;
            foreach (var (_, sectionId) in sections.Where(s => s.Board == source.SourceBoardId).OrderBy(s => s.Section, StringComparer.Ordinal))
                Add(PinterestUrls.SectionUrl(source.SourceUrl, sectionId));
        }
        return targets;
    }

    /// <summary>The content hashes of a board's assets <b>across its whole subtree</b> (sections / sub-folders) —
    /// for evicting its cached thumbnails, and as the board's true non-empty test (e.g. the failed-import discard
    /// must not see a board whose pins all landed in sections as empty).</summary>
    public async Task<IReadOnlyList<string>> GetBoardAssetShasAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, collectionId, ct);
        return await db.CollectionItems
            .Where(ci => subtreeIds.Contains(ci.CollectionId))
            .Select(ci => ci.Asset.Sha256)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>Detail for one board's Edit popup (per-kind counts, total size, created date, source ref).</summary>
    public async Task<BoardDetail?> GetBoardDetailAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null) return null;

        // Counts span the board's whole subtree (its sections / sub-folders), matching the card.
        var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, collectionId, ct);

        // One round-trip for all four kind/size aggregates (GroupBy(_ => 1) → conditional COUNT/SUM) rather
        // than four sequential count/sum queries.
        var agg = await db.CollectionItems
            .Where(ci => subtreeIds.Contains(ci.CollectionId) && ci.Asset.DeletedAt == null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Images = g.Count(ci => ci.Asset.Kind == MediaKind.Image),
                Gifs = g.Count(ci => ci.Asset.Kind == MediaKind.Gif),
                Videos = g.Count(ci => ci.Asset.Kind == MediaKind.Video),
                Size = g.Sum(ci => (long?)ci.Asset.Bytes) ?? 0L,
            })
            .FirstOrDefaultAsync(ct);

        var sourceRows = await db.CollectionSources
            .Where(s => s.CollectionId == collectionId)
            // Id is monotonic with insert order (≈ AddedAt) and an integer SQLite can ORDER BY — it can't
            // sort a DateTimeOffset column. Every provenance-bearing board has a source row (the v3 backfill
            // covers legacy boards; ingest writes one going forward), so no denormalised-pointer fallback.
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.SourceBoardId, s.SourceUrl, s.Name })
            .ToListAsync(ct);

        // Live images attributed to each source — what removing that source deletes (now subtree-scoped, so a
        // source's pins filed into section folders count toward it, matching remove-source's subtree sweep). Only
        // needed when the board merges ≥2 sources: a single-source board's source shows the board's whole live
        // total (removing it sweeps the subtree, see below) and a sourceless board has nothing to attribute — so
        // the per-source query is skipped in both cases.
        var totalLive = (agg?.Images ?? 0) + (agg?.Gifs ?? 0) + (agg?.Videos ?? 0);
        var countBySource = sourceRows.Count > 1
            ? (await db.CollectionItems
                .Where(ci => subtreeIds.Contains(ci.CollectionId) && ci.CollectionSourceId != null && ci.Asset.DeletedAt == null)
                .GroupBy(ci => ci.CollectionSourceId!.Value)
                .Select(g => new { SourceId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
                .ToDictionary(x => x.SourceId, x => x.Count)
            : new Dictionary<int, int>();

        var sources = sourceRows
            .Select(s => new BoardSource(s.Id, s.SourceBoardId, s.SourceUrl, s.Name,
                sourceRows.Count == 1 ? totalLive : countBySource.GetValueOrDefault(s.Id)))
            .ToList();

        return new BoardDetail(
            agg?.Images ?? 0, agg?.Gifs ?? 0, agg?.Videos ?? 0, agg?.Size ?? 0L,
            collection.CreatedAt, sources);
    }

    /// <param name="collectionId">Filter to one collection, or null for the whole library.</param>
    /// <param name="search">Free-text filter over title, description, and tags. Null/blank = no filter.</param>
    public async Task<IReadOnlyList<AssetView>> GetAssetsAsync(
        int? collectionId, string? search = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<Asset> query = db.Assets;
        if (collectionId is int id)
            query = query.Where(a => a.CollectionItems.Any(ci => ci.CollectionId == id));
        query = ApplySearch(query, search);

        var rows = await query
            // Stable base order (Id is monotonic with import order); the deterministic ordering by pin id is
            // applied below.
            .OrderByDescending(a => a.Id)
            .Select(a => new
            {
                a.Id, a.RelativePath, a.Kind, a.Title, a.Description, a.SourceUrl, a.Width, a.Height, a.Sha256,
                a.DeletedAt, a.DeletionNote, a.SourceId
            })
            .ToListAsync(ct);

        // Order by the Pinterest pin id (newest first), not local import order — the id is fixed per pin, so a
        // re-imported or restored pin keeps its place instead of jumping to the front, and Pinterest ids are
        // roughly chronological. (The sidecar carries no per-pin date, so the id is the best stable signal.)
        // Sorted in memory as a number — SQLite would compare the id column as text — with OrderByDescending
        // stable, so any pins without a numeric id keep the Id-desc order above and sort last.
        return rows
            .OrderByDescending(a => ParsePinId(a.SourceId))
            .Select(a => new AssetView(
                a.Id, _store.GetAbsolutePath(a.RelativePath), a.Kind, a.Title, a.Description,
                a.SourceUrl, a.Width, a.Height, a.Sha256, a.DeletedAt is not null, a.DeletionNote))
            .ToList();
    }

    /// <summary>
    /// Apply a case-insensitive substring filter across title, description, and tag names. Uses LIKE
    /// (fast enough for a personal library); FTS5 is the future upgrade path if archives get huge.
    /// </summary>
    private static IQueryable<Asset> ApplySearch(IQueryable<Asset> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return query;

        // Escape LIKE wildcards in user input so '%' and '_' are matched literally.
        var escaped = search.Trim()
            .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = $"%{escaped}%";

        return query.Where(a =>
            EF.Functions.Like(a.Title!, pattern, "\\")
            || EF.Functions.Like(a.Description!, pattern, "\\")
            || a.AssetTags.Any(at => EF.Functions.Like(at.Tag.Name, pattern, "\\")));
    }

    // Pinterest pin ids are big integers; parse for numeric ordering. Missing/non-numeric ids sort last.
    private static long ParsePinId(string? sourceId) => long.TryParse(sourceId, out var id) ? id : long.MinValue;

    public async Task<int> GetAssetCountAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Assets.CountAsync(ct);
    }

    /// <summary>
    /// Up to <paramref name="count"/> cover thumbnails for a board's grid card (the SHA + blob path of live
    /// image/GIF assets), picked <b>spread across the collection</b> — the most recent, the midpoint(s), and the
    /// oldest — rather than the N newest (which cluster in the last import / one board). That gives a board's
    /// 3-up its variety and, for the "All images" card (<paramref name="collectionId"/> null), pulls from across
    /// the project's whole history so the covers come from different boards. A board's covers span its <b>whole
    /// subtree</b>, so a board whose pins are all sectioned still gets a cover.
    /// </summary>
    public async Task<IReadOnlyList<(string Sha256, string AbsolutePath)>> GetCoverAssetsAsync(
        int? collectionId, int count, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<Asset> query = db.Assets
            .Where(a => a.DeletedAt == null && (a.Kind == MediaKind.Image || a.Kind == MediaKind.Gif));
        if (collectionId is int id)
        {
            var subtreeIds = await CollectionTree.SubtreeIdsAsync(db, id, ct);
            query = query.Where(a => a.CollectionItems.Any(ci => subtreeIds.Contains(ci.CollectionId)));
        }

        var n = await query.CountAsync(ct);
        if (n == 0) return Array.Empty<(string, string)>();

        // Fetch only the spread positions (most recent → oldest) — never the whole id list. Each position is
        // seeked from whichever end is nearer (desc from the top, asc from the bottom) so the deepest Skip is
        // ~n/2, and only `count` single rows are ever materialised.
        var covers = new List<(string Sha256, string AbsolutePath)>(count);
        foreach (var p in SpreadSelect.Positions(n, count))
        {
            var ordered = p <= n - 1 - p
                ? query.OrderByDescending(a => a.Id).Skip(p)
                : query.OrderBy(a => a.Id).Skip(n - 1 - p);
            var row = await ordered.Take(1).Select(a => new { a.Sha256, a.RelativePath }).FirstOrDefaultAsync(ct);
            if (row is not null) covers.Add((row.Sha256, _store.GetAbsolutePath(row.RelativePath)));
        }
        return covers;
    }

    /// <summary>Full metadata for one asset (for the detail panel), or null if it no longer exists.</summary>
    public async Task<AssetDetail?> GetAssetDetailAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Assets
            .Where(a => a.Id == id)
            .Select(a => new
            {
                a.Id, a.RelativePath, a.Kind, a.MimeType, a.Title, a.Description,
                a.SourceUrl, a.OriginalUrl, a.SourceId, a.Width, a.Height, a.Bytes,
                a.CreatedAt, a.ImportedAt, a.DeletedAt, a.DeletionNote,
                // The shown board title is DisplayName ?? Name (the local rename override), same as the grid.
                Boards = a.CollectionItems.Select(ci => ci.Collection.DisplayName ?? ci.Collection.Name).ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        return new AssetDetail(
            row.Id, _store.GetAbsolutePath(row.RelativePath), row.Kind, row.MimeType,
            row.Title, row.Description, row.SourceUrl, row.OriginalUrl, row.SourceId,
            row.Width, row.Height, row.Bytes, row.CreatedAt, row.ImportedAt, row.Boards,
            row.DeletedAt is not null, row.DeletionNote);
    }
}
