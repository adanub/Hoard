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

    public async Task<IReadOnlyList<CollectionView>> GetCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Count + total bytes of the board's live (non-tombstoned) assets — for the card's "N images · X" meta.
        return await db.Collections
            .OrderBy(c => c.DisplayName ?? c.Name)
            .Select(c => new CollectionView(
                c.Id, c.DisplayName ?? c.Name,
                c.Items.Count(ci => ci.Asset.DeletedAt == null),
                c.Items.Where(ci => ci.Asset.DeletedAt == null).Sum(ci => (long?)ci.Asset.Bytes) ?? 0L))
            .ToListAsync(ct);
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

    /// <summary>The content hashes of a board's assets (for evicting its cached thumbnails).</summary>
    public async Task<IReadOnlyList<string>> GetBoardAssetShasAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CollectionItems
            .Where(ci => ci.CollectionId == collectionId)
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

        // One round-trip for all four kind/size aggregates (GroupBy(_ => 1) → conditional COUNT/SUM) rather
        // than four sequential count/sum queries.
        var agg = await db.CollectionItems
            .Where(ci => ci.CollectionId == collectionId && ci.Asset.DeletedAt == null)
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

        // Live images attributed to each source — what removing that source deletes. When the board has a
        // single source, removing it sweeps the whole board (incl. un-attributed legacy pins), so that source's
        // count is the board's total live pins.
        var countBySource = (await db.CollectionItems
            .Where(ci => ci.CollectionId == collectionId && ci.CollectionSourceId != null && ci.Asset.DeletedAt == null)
            .GroupBy(ci => ci.CollectionSourceId!.Value)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToListAsync(ct))
            .ToDictionary(x => x.SourceId, x => x.Count);
        var totalLive = (agg?.Images ?? 0) + (agg?.Gifs ?? 0) + (agg?.Videos ?? 0);

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
    /// Up to <paramref name="count"/> cover thumbnails (newest first) for a board's grid card: the SHA + blob
    /// path of recent live image/GIF assets. <paramref name="collectionId"/> null = across the whole project
    /// (the "All images" card). Lean projection — no full asset load — since it runs once per board card.
    /// </summary>
    public async Task<IReadOnlyList<(string Sha256, string AbsolutePath)>> GetCoverAssetsAsync(
        int? collectionId, int count, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<Asset> query = db.Assets
            .Where(a => a.DeletedAt == null && (a.Kind == MediaKind.Image || a.Kind == MediaKind.Gif));
        if (collectionId is int id)
            query = query.Where(a => a.CollectionItems.Any(ci => ci.CollectionId == id));

        var rows = await query
            .OrderByDescending(a => a.Id)
            .Take(count)
            .Select(a => new { a.Sha256, a.RelativePath })
            .ToListAsync(ct);
        return rows.Select(r => (r.Sha256, _store.GetAbsolutePath(r.RelativePath))).ToList();
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
