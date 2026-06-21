using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

/// <summary>Helpers for the Collection parent/child tree (a board → its folders → their sub-folders → …).</summary>
internal static class CollectionTree
{
    /// <summary>The collection plus every descendant (its child folders, recursively). Breadth-first over
    /// <c>ParentId</c>; guards against cycles defensively. Used to delete a board with its whole subtree and to
    /// pre-skip held section pins on re-import.</summary>
    public static async Task<List<int>> SubtreeIdsAsync(HoardDbContext db, int rootId, CancellationToken ct)
    {
        var ids = new List<int> { rootId };
        var frontier = new List<int> { rootId };
        while (frontier.Count > 0)
        {
            var children = await db.Collections
                .Where(c => c.ParentId != null && frontier.Contains(c.ParentId.Value))
                .Select(c => c.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            children = children.Where(id => !ids.Contains(id)).ToList();
            if (children.Count == 0) break;
            ids.AddRange(children);
            frontier = children;
        }
        return ids;
    }
}
