namespace Hoard.Core.Library;

/// <summary>
/// Even-spread index selection for an "N-up" collage: pick <c>count</c> items spread across an ordered list —
/// index 0 (first), the midpoint(s), and the last — rather than the first <c>count</c> (which cluster). Shared
/// by the DB cover query and the launcher's cached-thumbnail collage so the two pick covers the same way.
/// </summary>
public static class SpreadSelect
{
    /// <summary><paramref name="count"/> evenly-spaced positions across <c>[0, n-1]</c> inclusive — 0 (first),
    /// the midpoint(s), and <c>n-1</c> (last). Returns every position when <c>n ≤ count</c>; empty when either is
    /// ≤ 0. Positions are strictly increasing (no duplicates) for the produced inputs.</summary>
    public static IEnumerable<int> Positions(int n, int count)
    {
        if (n <= 0 || count <= 0) yield break;
        if (n <= count) { for (var i = 0; i < n; i++) yield return i; yield break; }
        if (count == 1) { yield return 0; yield break; }
        for (var j = 0; j < count; j++)
            yield return (int)System.Math.Round((double)j * (n - 1) / (count - 1), System.MidpointRounding.AwayFromZero);
    }
}
