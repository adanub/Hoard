using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

/// <summary>
/// One-time data repair for boards imported before per-pin provenance (v4): assign each un-attributed
/// <c>CollectionItem.CollectionSourceId</c>. A single-source board is unambiguous (every pin came from its one
/// source); a merged board is split by the originating board id recorded in each asset's stored sidecar, so
/// removing one source removes exactly its own images even on pre-v4 data. Idempotent — only touches links that
/// are still un-attributed and whose board id resolves to one of the board's sources.
/// </summary>
internal static class SourceAttributionBackfill
{
    public static async Task RunAsync(HoardDbContext db, CancellationToken ct = default)
    {
        var collectionIds = await db.CollectionItems
            .Where(ci => ci.CollectionSourceId == null)
            .Select(ci => ci.CollectionId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var collectionId in collectionIds)
        {
            var sources = await db.CollectionSources
                .Where(s => s.CollectionId == collectionId)
                .Select(s => new { s.Id, s.SourceBoardId })
                .ToListAsync(ct).ConfigureAwait(false);
            if (sources.Count == 0) continue; // a local board — nothing to attribute to

            if (sources.Count == 1)
            {
                // Unambiguous: every link in a single-source board came from that one source.
                var only = sources[0].Id;
                await db.CollectionItems
                    .Where(ci => ci.CollectionId == collectionId && ci.CollectionSourceId == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(ci => ci.CollectionSourceId, only), ct).ConfigureAwait(false);
                continue;
            }

            // Merged board: attribute each link by the board id stored in its asset's sidecar.
            var sourceByBoard = new Dictionary<string, int>();
            foreach (var s in sources)
                if (s.SourceBoardId is { } board) sourceByBoard[board] = s.Id;

            var items = await db.CollectionItems
                .Where(ci => ci.CollectionId == collectionId && ci.CollectionSourceId == null)
                .Select(ci => new { ci.Id, ci.Asset.MetadataJson })
                .ToListAsync(ct).ConfigureAwait(false);

            // Group the resolved links by source so each source is one UPDATE.
            var idsBySource = new Dictionary<int, List<int>>();
            foreach (var item in items)
            {
                var boardId = SidecarBoardId.From(item.MetadataJson);
                if (boardId is null || !sourceByBoard.TryGetValue(boardId, out var sourceId)) continue;
                if (!idsBySource.TryGetValue(sourceId, out var ids)) idsBySource[sourceId] = ids = new List<int>();
                ids.Add(item.Id);
            }
            foreach (var (sourceId, ids) in idsBySource)
                await db.CollectionItems
                    .Where(ci => ids.Contains(ci.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(ci => ci.CollectionSourceId, sourceId), ct).ConfigureAwait(false);
        }
    }
}
