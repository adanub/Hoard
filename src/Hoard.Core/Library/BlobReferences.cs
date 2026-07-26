using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Library;

/// <summary>
/// The ONE last-live-referrer rule for blob freeing: rows aren't unique per content (pin identity),
/// so a blob may be shared and must be freed only when its last live referrer goes. Every path that
/// frees/deletes a blob — tombstone, remove-source/delete-board sweeps, the ingest tombstone-skip —
/// routes through here. Comparison is separator-canonical: legacy Windows rows can hold
/// <c>ab\cd\…</c> while replayed/new rows hold <c>ab/cd/…</c>, and a raw string compare would declare
/// a shared blob unreferenced and destroy it out from under the surviving pin.
/// </summary>
internal static class BlobReferences
{
    /// <summary>Of <paramref name="relativePaths"/>, the (distinct, as-given) ones no LIVE row still
    /// references. Call after the commit that removed/tombstoned the referrers being deleted.</summary>
    public static async Task<List<string>> UnreferencedAsync(
        HoardDbContext db, IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        var distinct = relativePaths.Distinct().ToList();
        if (distinct.Count == 0) return distinct;

        // Query with BOTH separator spellings of every candidate (SQLite can't canonicalise in-query),
        // then compare canonically.
        var spellings = distinct
            .SelectMany(p => new[] { p.Replace('\\', '/'), p.Replace('/', '\\') })
            .Distinct()
            .ToList();
        var stillHeld = (await db.Assets
                .Where(a => a.DeletedAt == null && spellings.Contains(a.RelativePath))
                .Select(a => a.RelativePath)
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(Canonical)
            .ToHashSet();
        return distinct.Where(p => !stillHeld.Contains(Canonical(p))).ToList();
    }

    /// <summary>True when any LIVE row other than <paramref name="excludeAssetId"/> references the blob.</summary>
    public static async Task<bool> IsSharedAsync(
        HoardDbContext db, string relativePath, int excludeAssetId, CancellationToken ct)
    {
        var spellings = new[] { relativePath.Replace('\\', '/'), relativePath.Replace('/', '\\') }.Distinct().ToList();
        return await db.Assets
            .AnyAsync(a => a.Id != excludeAssetId && a.DeletedAt == null && spellings.Contains(a.RelativePath), ct)
            .ConfigureAwait(false);
    }

    private static string Canonical(string relativePath) => relativePath.Replace('\\', '/');
}
