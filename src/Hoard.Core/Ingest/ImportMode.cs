namespace Hoard.Core.Ingest;

/// <summary>
/// How thoroughly an import crawls. The split exists because a re-sync of an already-archived board spends
/// nearly all its time proving it has nothing to do — paging the whole source listing, and stat'ing every
/// blob it already holds — so the everyday case gets a delta and the exhaustive case stays one click away.
/// Same pipeline either way: only how much it looks at differs, never what it does with what it finds.
/// </summary>
public enum ImportMode
{
    /// <summary>
    /// Crawl every target to its end, let the connector discover sub-collections itself, and verify each
    /// held pin's blob is still intact on disk (a missing/torn one is re-downloaded). The only correct
    /// mode for a first import, and what a "full sync" runs to pick up what a delta structurally can't
    /// see — a section added at the source, or an item that isn't near the front of the listing.
    /// </summary>
    Full,

    /// <summary>
    /// Stop each target once it reaches a run of items already held, and trust the index about what's on
    /// disk. Costs a page or two per target instead of the whole board.
    /// </summary>
    Delta,
}
