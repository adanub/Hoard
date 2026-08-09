namespace Hoard.Core.Connectors;

/// <summary>Authentication / behaviour options passed to a connector for one download.</summary>
public sealed record ConnectorOptions
{
    /// <summary>Browser to lift cookies from (e.g. "firefox", "chrome") for private/own boards.</summary>
    public string? CookiesFromBrowser { get; init; }

    /// <summary>Path to a Netscape-format cookies.txt, as an alternative to <see cref="CookiesFromBrowser"/>.</summary>
    public string? CookiesFile { get; init; }

    /// <summary>Rate-limiting / politeness controls for the download. Defaults are gentle on the source.</summary>
    public RateLimitOptions RateLimit { get; init; } = new();

    /// <summary>Cap the number of items pulled (maps to gallery-dl <c>--range 1-N</c>). Null = the whole board.</summary>
    public int? MaxItems { get; init; }

    /// <summary>
    /// Path to a per-project archive of already-fetched source items. When set, a connector should
    /// record each fetched item and skip re-downloading ones already recorded — so re-importing a
    /// board only pulls what's new. Null disables the optimisation (everything is re-fetched).
    /// </summary>
    public string? DownloadArchivePath { get; init; }

    /// <summary>
    /// The items the library already tracks (live <i>and</i> tombstoned), so the connector can rebuild
    /// its skip-archive from this single source of truth before each run rather than trusting a separate
    /// list that could silently drift. Null leaves any existing archive untouched (e.g. a restore, which
    /// must re-fetch one item regardless of what's recorded).
    /// </summary>
    public IReadOnlyCollection<KnownSourceItem>? KnownItems { get; init; }

    /// <summary>
    /// Stop crawling a target once this many <b>consecutive</b> items have been pre-skipped as already
    /// known, instead of enumerating it to the end. Sources list newest-first, so what's new front-loads
    /// and a run of known items means we've reached the part already archived — the difference between a
    /// re-sync costing one page and costing the whole board. A fresh item resets the run. Null (the
    /// default) enumerates everything, which is what a first import and a full re-sync want.
    /// <para>The trade: a target whose listing <i>isn't</i> newest-first (a source-side custom sort) can
    /// hide new items behind a longer run of known ones, so this belongs to an incremental sync — never
    /// to the crawl that has to be exhaustive.</para>
    /// </summary>
    public int? StopAfterConsecutiveKnown { get; init; }

    /// <summary>
    /// Whether the connector should follow a target's <i>sub-collections</i> (for Pinterest, a board's
    /// sections) on its own. Off when the caller passes each sub-collection as its own crawl target —
    /// which is what an incremental sync does, because a connector that appends sub-collections after
    /// the parent's items would have them cut off by <see cref="StopAfterConsecutiveKnown"/>. A connector
    /// with no such concept ignores this.
    /// </summary>
    public bool IncludeSubCollections { get; init; } = true;
}

/// <summary>One source item the library already knows about, used to rebuild a connector's skip-archive.</summary>
/// <param name="BoardId">The source board/section id the item was found under.</param>
/// <param name="SourceId">The item's stable source id (e.g. the Pinterest pin id).</param>
public readonly record struct KnownSourceItem(string BoardId, string SourceId);

/// <summary>
/// Throttling applied to a download so we don't hammer the source (and trip its abuse defences).
/// These map onto gallery-dl's sleep / rate flags. Each interval also has a random jitter added on
/// top per item (gallery-dl picks a value in <c>[base, base+jitter]</c>) so the cadence looks less
/// robotic — the base is the floor, jitter is the random extra.
/// </summary>
public sealed record RateLimitOptions
{
    /// <summary>Minimum seconds between HTTP requests during extraction (gallery-dl <c>--sleep-request</c>).</summary>
    public double RequestIntervalSeconds { get; init; } = 1.0;

    /// <summary>Random extra (0..this) added to each request interval.</summary>
    public double RequestIntervalJitterSeconds { get; init; } = 1.0;

    /// <summary>Minimum seconds before each file download (gallery-dl <c>--sleep</c>).</summary>
    public double DownloadIntervalSeconds { get; init; } = 0.5;

    /// <summary>Random extra (0..this) added to each download interval.</summary>
    public double DownloadIntervalJitterSeconds { get; init; } = 1.0;

    /// <summary>Seconds to back off when the source returns HTTP 429 (gallery-dl <c>--sleep-429</c>).</summary>
    public double TooManyRequestsBackoffSeconds { get; init; } = 60.0;

    /// <summary>Optional bandwidth cap, e.g. "2M" or "500k" (gallery-dl <c>--limit-rate</c>). Null = uncapped.</summary>
    public string? MaxRate { get; init; }
}

/// <summary>One downloaded media file plus the metadata parsed from its sidecar.</summary>
public sealed record SourceMediaItem
{
    public required string FilePath { get; init; }
    public required string Connector { get; init; }
    public string? SourceId { get; init; }
    public string? SourceUrl { get; init; }
    public string? OriginalUrl { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? BoardName { get; init; }
    public string? BoardId { get; init; }
    public string? BoardUrl { get; init; }

    /// <summary>The source <i>section</i> (a sub-folder within the board) this item sits in, when it's inside
    /// one — so ingest files it into a matching child folder instead of the board's main grid. Null for a loose
    /// (sectionless) item.</summary>
    public string? SectionId { get; init; }
    public string? SectionName { get; init; }
    public string? SectionUrl { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? RawJson { get; init; }
}

/// <summary>An ingestion source. Pinterest is the only one, and the only one planned — this interface
/// exists to keep subprocess-spawning code out of platform-neutral Core (the real implementation lives in
/// Hoard.Ingest.GalleryDl) and to give the tests a seam to fake, not as a plug-in contract.</summary>
public interface ISourceConnector
{
    string Name { get; }
    bool CanHandle(string url);

    /// <summary>
    /// Download from <paramref name="url"/>, invoking <paramref name="onItem"/> for each item as soon
    /// as it finishes downloading (so callers can ingest and display incrementally rather than waiting
    /// for the whole batch). The connector owns and cleans up any temporary files; each item's
    /// <see cref="SourceMediaItem.FilePath"/> stays valid for the duration of the awaited callback.
    /// </summary>
    Task DownloadAsync(
        string url,
        ConnectorOptions options,
        IProgress<string>? log,
        Func<SourceMediaItem, CancellationToken, Task> onItem,
        CancellationToken ct);

    /// <summary>
    /// Crawl <b>several</b> targets in one run. A board and each of its sub-collections are separate
    /// targets, so a sync of one board is one call — and a connector that pays a fixed per-run cost
    /// (spawning a tool, lifting cookies) pays it once instead of once per target. Each target is
    /// independent: its own <see cref="ConnectorOptions.StopAfterConsecutiveKnown"/> run, and one target
    /// finding nothing never stops the next.
    /// <para>The default implementation just runs them in order, which is exactly right for a connector
    /// with no per-run overhead to amortise.</para>
    /// </summary>
    async Task DownloadAsync(
        IReadOnlyList<string> urls,
        ConnectorOptions options,
        IProgress<string>? log,
        Func<SourceMediaItem, CancellationToken, Task> onItem,
        CancellationToken ct)
    {
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            await DownloadAsync(url, options, log, onItem, ct).ConfigureAwait(false);
        }
    }
}
