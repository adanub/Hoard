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
}

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
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? RawJson { get; init; }
}

/// <summary>A pluggable ingestion source (Pinterest first; more to come behind the same contract).</summary>
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
}
