using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Tests;

/// <summary>
/// One normalised, sorted, line-per-fact projection of everything the archive op log must carry —
/// equal projections = equivalent archives (and a readable diff when they're not). Shared by the
/// round-trip (P1) and two-device convergence (P2) tests.
/// </summary>
internal static class ArchiveTestProjection
{
    public static async Task<string> ProjectAsync(TestDbContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var lines = new List<string>();

        foreach (var a in await db.Assets.Include(x => x.AssetTags).ThenInclude(x => x.Tag).AsNoTracking().ToListAsync())
            lines.Add($"asset|{a.Sha256}|{a.RelativePath}|{a.MimeType}|{a.Kind}|{a.Width}|{a.Height}|{a.Bytes}" +
                      $"|{a.SourceConnector}|{a.SourceId}|{a.SourceUrl}|{a.OriginalUrl}|{a.Title}|{a.Description}" +
                      $"|{a.CreatedAt:O}|{a.ImportedAt:O}|{a.DeletedAt:O}|{a.DeletionNote}" +
                      $"|tags={string.Join(",", a.AssetTags.Select(at => at.Tag.Name).OrderBy(n => n, StringComparer.Ordinal))}" +
                      $"|{a.MetadataJson}");

        foreach (var c in await db.Collections.Include(x => x.Parent).AsNoTracking().ToListAsync())
            lines.Add($"collection|{c.Uid}|{c.Name}|{c.DisplayName}|{c.SourceConnector}|{c.SourceBoardId}" +
                      $"|{c.SourceUrl}|{c.SourceSectionId}|{c.CreatedAt:O}|parent={c.Parent?.Uid}");

        foreach (var s in await db.CollectionSources.Include(x => x.Collection).AsNoTracking().ToListAsync())
            lines.Add($"source|{s.Uid}|{s.Collection.Uid}|{s.SourceConnector}|{s.SourceBoardId}|{s.SourceUrl}|{s.Name}|{s.AddedAt:O}");

        foreach (var l in await db.CollectionItems
                     .Include(x => x.Asset).Include(x => x.Collection).Include(x => x.CollectionSource)
                     .AsNoTracking().ToListAsync())
            lines.Add($"link|{l.Asset.Sha256}|{l.Collection.Uid}|{l.CollectionSource?.Uid}|{l.Note}|{l.AddedAt:O}");

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }
}
