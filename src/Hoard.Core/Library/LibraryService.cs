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

public sealed record CollectionView(int Id, string Name, int ItemCount);

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
        return await db.Collections
            .OrderBy(c => c.Name)
            .Select(c => new CollectionView(c.Id, c.Name, c.Items.Count))
            .ToListAsync(ct);
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
                Boards = a.CollectionItems.Select(ci => ci.Collection.Name).ToList(),
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
