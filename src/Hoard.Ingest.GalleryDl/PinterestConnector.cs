using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Hoard.Core.Connectors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Hoard.Ingest.GalleryDl;

/// <summary>
/// Pinterest connector backed by the bundled gallery-dl executable. It downloads a board/section/pin
/// URL into a temp directory with per-file metadata sidecars, then pairs each media file with its
/// <c>.json</c> twin and parses it via <see cref="PinterestSidecarParser"/>.
///
/// Desktop/server-only: it spawns a subprocess, which mobile platforms forbid — mobile clients reach
/// ingestion through the server instead.
/// </summary>
public sealed partial class PinterestConnector : ISourceConnector
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".mp4", ".webm", ".m4v", ".mov",
    };

    private readonly string _galleryDlPath;
    private readonly ILogger<PinterestConnector> _logger;

    /// <param name="galleryDlPath">Path to gallery-dl(.exe), or just "gallery-dl" to resolve via PATH.</param>
    public PinterestConnector(string galleryDlPath, ILogger<PinterestConnector>? logger = null)
    {
        _galleryDlPath = galleryDlPath;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PinterestConnector>.Instance;
    }

    public string Name => PinterestSidecarParser.ConnectorName;

    public bool CanHandle(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Host.Contains("pinterest.", StringComparison.OrdinalIgnoreCase)
               || u.Host.Equals("pin.it", StringComparison.OrdinalIgnoreCase));

    public Task DownloadAsync(
        string url, ConnectorOptions options, IProgress<string>? log,
        Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        => DownloadAsync([url], options, log, onItem, ct);

    /// <summary>
    /// Crawl every target in ONE gallery-dl process. gallery-dl runs each input URL as its own extractor —
    /// its own early-stop budget, and an empty or failing one never stops the next — so a board and its
    /// sections cost one process start and one cookie extraction between them rather than one each.
    /// </summary>
    public async Task DownloadAsync(
        IReadOnlyList<string> urls, ConnectorOptions options, IProgress<string>? log,
        Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
    {
        if (urls.Count == 0) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "hoard-dl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo(_galleryDlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Rebuild the skip-archive from what the library actually tracks (the single source of truth)
            // so it can't silently drift, then let gallery-dl skip those and append new ones.
            if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath) && options.KnownItems is { } known)
                RegenerateArchive(options.DownloadArchivePath, known);
            foreach (var arg in BuildArguments(tempDir, urls, options))
                psi.ArgumentList.Add(arg);

            // Log every target, not just a count: "did this sync even ask for that folder?" is the first
            // question when a delta looks like it missed something.
            _logger.LogInformation(
                "Running gallery-dl for {Count} target(s) into {TempDir}{Stop}: {Targets}",
                urls.Count, tempDir,
                options.StopAfterConsecutiveKnown is int n ? $", stopping after {n} consecutive known" : "",
                string.Join(", ", urls));

            // Keep a rolling tail of stderr so a failure can report *why*, not just a number. The
            // not-found count is kept separately and UNCAPPED: it's compared against the number of targets,
            // which a ten-line tail couldn't answer for a board with more sections than that.
            var errorTail = new Queue<string>();
            var notFound = 0;
            var skipped = 0;
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogInformation("gallery-dl: {Line}", e.Data);
                log?.Report(e.Data);
                // gallery-dl prints "# <path>" for every item it skipped as already-archived. That's the
                // proof a target's listing was actually reachable — see the diagnosis below.
                if (e.Data.StartsWith('#')) skipped++;
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogWarning("gallery-dl: {Line}", e.Data);
                log?.Report(e.Data);
                errorTail.Enqueue(e.Data);
                while (errorTail.Count > 10) errorTail.Dequeue();
                if (IsNotFound(e.Data)) notFound++;
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                // Name the path we actually looked at and the way to fix it: this is what a clone that never
                // fetched the binary hits, and "is it installed?" sends you looking in the wrong places.
                throw new InvalidOperationException(
                    $"Couldn't start the downloader (gallery-dl), looked for it at '{_galleryDlPath}'. " +
                    "It's bundled next to the app in a release; running from source, building fetches it — " +
                    "or run tools/fetch-gallery-dl.ps1 to fetch it by hand.", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Poll the temp dir while gallery-dl runs, handing off each item as soon as it lands, then
            // sweep once more after exit to catch anything written between the last poll and shutdown.
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var exitTask = process.WaitForExitAsync(ct);
            do
            {
                count += await ProcessNewAsync(tempDir, handled, onItem, log, ct).ConfigureAwait(false);
                if (!exitTask.IsCompleted)
                    await Task.WhenAny(exitTask, Task.Delay(400, ct)).ConfigureAwait(false);
            }
            while (!exitTask.IsCompleted);
            await exitTask.ConfigureAwait(false);
            count += await ProcessNewAsync(tempDir, handled, onItem, log, ct).ConfigureAwait(false);

            _logger.LogInformation("gallery-dl exit {Code}; produced {Count} media items for {Count2} target(s)",
                process.ExitCode, count, urls.Count);

            if (count > 0) return;

            // Nothing came back. Distinguish a real dead-end from "everything was already archived".
            var detail = errorTail.Count > 0 ? " " + string.Join(" | ", errorTail) : "";

            // A "not found" on a board that exists almost always means it's private and the request was
            // unauthenticated — point at the cookies, which is the real fix. But a run crawls a board AND
            // each of its sections, and a section deleted at the source 404s on every sync from then on: a
            // sync that lost one folder must not report itself as a dead board.
            //
            // Which of the two it is can't be settled by counting error LINES against targets (one failing
            // target can log several, which would read as "they all failed"). The reliable evidence is the
            // opposite thing: a target we DID reach prints "# <path>" per already-archived item. So —
            //   • skipped > 0  → at least one listing was walked, so the credentials work and the archive is
            //                    current. Any not-found alongside that is a target that has gone, not a dead
            //                    board: log it and report up to date.
            //   • skipped == 0 → nothing was reachable at all. With an error to go with it, that's the
            //                    private-board/cookies case, whatever the target count.
            var reachedSomething = skipped > 0;
            if (notFound > 0 && !reachedSomething)
                throw new InvalidOperationException(
                    "Pinterest returned \"board not found\". If the board is private, select the browser " +
                    "you're logged into Pinterest with in the Cookies dropdown (Firefox-based browsers " +
                    "like Zen are supported)." + detail);

            // A non-zero exit with nothing downloaded AND nothing skipped means the run achieved nothing —
            // report it. If some targets were walked, the failure was partial and the archive is still current.
            if (process.ExitCode != 0 && !reachedSomething)
                throw new InvalidOperationException(
                    $"gallery-dl failed (exit {process.ExitCode}) and downloaded nothing.{detail}");

            // Nothing new: with an archive in use this just means the board is already fully backed up — a
            // normal, successful re-import, not an error.
            if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath))
            {
                if (notFound > 0)
                    _logger.LogWarning(
                        "Some of the {Total} target(s) were not found — a section removed at the source? " +
                        "The rest are up to date ({Skipped} item(s) already held).{Detail}",
                        urls.Count, skipped, detail);
                else
                    _logger.LogInformation(
                        "Nothing new across {Count} target(s); already up to date ({Skipped} already held).",
                        urls.Count, skipped);
                return;
            }

            throw new InvalidOperationException(
                "No media was found at that URL. If it's a private board, choose the browser you're " +
                "logged into Pinterest with in the Cookies dropdown." + detail);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    /// <summary>
    /// The full gallery-dl command line for one run, as an ordered argument list — pure, so the flags that
    /// decide how much of a board gets walked are unit-testable without spawning anything.
    /// </summary>
    internal static List<string> BuildArguments(string tempDir, IReadOnlyList<string> urls, ConnectorOptions options)
    {
        var args = new List<string>
        {
            "--write-metadata",  // emit <file>.json sidecars
            "--directory",       // flat output; we group by sidecar metadata, not path
            tempDir,
        };
        AddCookieArgs(args, options);
        AddRateLimitArgs(args, options.RateLimit);

        if (options.MaxItems is int max and > 0)
        {
            args.Add("--range");
            args.Add($"1-{max}");
        }

        // Stop walking a target once this many consecutive items were skipped as already-archived. Scoped to
        // the CURRENT extractor, so each input URL gets its own budget and an exhausted one simply moves on
        // to the next target (that's --abort; --terminate would take the rest of the run down with it).
        if (options.StopAfterConsecutiveKnown is int stopAfter and > 0)
        {
            args.Add("--abort");
            args.Add(stopAfter.ToString(CultureInfo.InvariantCulture));
        }

        // gallery-dl chains a board's sections AFTER every one of its pins, so they sit beyond where --abort
        // stops. When the caller is crawling each section as its own target, turn the board's own recursion
        // off: without this a board short enough not to trigger the stop would crawl them twice.
        if (!options.IncludeSubCollections)
        {
            args.Add("--option");
            args.Add("extractor.pinterest.sections=false");
        }

        if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath))
        {
            args.Add("--download-archive");
            args.Add(options.DownloadArchivePath);
        }

        // Targets last: gallery-dl runs each as its own extractor, in order.
        args.AddRange(urls);
        return args;
    }

    /// <summary>A gallery-dl error line reporting that a target doesn't exist (or isn't visible to us).</summary>
    private static bool IsNotFound(string line)
        => line.Contains("NotFoundError", StringComparison.OrdinalIgnoreCase)
           || line.Contains("could not be found", StringComparison.OrdinalIgnoreCase);

    private static void AddCookieArgs(List<string> args, ConnectorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(options.CookiesFromBrowser);
        }
        else if (!string.IsNullOrWhiteSpace(options.CookiesFile))
        {
            args.Add("--cookies");
            args.Add(options.CookiesFile);
        }
    }

    private static void AddRateLimitArgs(List<string> args, RateLimitOptions rate)
    {
        // Emit "min-max" when there's jitter so gallery-dl waits a random time in that range per item
        // (a less robotic cadence); a plain value otherwise.
        void AddSleep(string flag, double seconds, double jitter)
        {
            if (seconds <= 0 && jitter <= 0) return;
            var value = jitter > 0
                ? $"{seconds.ToString(CultureInfo.InvariantCulture)}-{(seconds + jitter).ToString(CultureInfo.InvariantCulture)}"
                : seconds.ToString(CultureInfo.InvariantCulture);
            args.Add(flag);
            args.Add(value);
        }

        AddSleep("--sleep-request", rate.RequestIntervalSeconds, rate.RequestIntervalJitterSeconds);
        AddSleep("--sleep", rate.DownloadIntervalSeconds, rate.DownloadIntervalJitterSeconds);
        AddSleep("--sleep-429", rate.TooManyRequestsBackoffSeconds, jitter: 0);
        if (!string.IsNullOrWhiteSpace(rate.MaxRate))
        {
            args.Add("--limit-rate");
            args.Add(rate.MaxRate);
        }
    }

    /// <summary>
    /// Scan the temp dir for completed items not yet handled and hand each to <paramref name="onItem"/>.
    /// A pin is "complete" once its <c>.json</c> sidecar exists (gallery-dl writes it after the media),
    /// so keying on sidecars avoids picking up half-written files. Returns how many were emitted.
    /// </summary>
    private async Task<int> ProcessNewAsync(
        string tempDir, HashSet<string> handled,
        Func<SourceMediaItem, CancellationToken, Task> onItem, IProgress<string>? log, CancellationToken ct)
    {
        var emitted = 0;
        foreach (var sidecar in Directory.EnumerateFiles(tempDir, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!handled.Add(sidecar)) continue; // already processed this one

            var mediaPath = sidecar[..^".json".Length]; // strip ".json" → the media file it describes
            if (!MediaExtensions.Contains(Path.GetExtension(mediaPath)) || !File.Exists(mediaPath))
                continue; // e.g. an unmuxed video whose merged file was never produced (needs ffmpeg)

            if (YtdlFragmentRegex().IsMatch(Path.GetFileName(mediaPath)))
                continue;

            SourceMediaItem item;
            try
            {
                item = PinterestSidecarParser.Parse(mediaPath, await File.ReadAllTextAsync(sidecar, ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse sidecar for {File}; importing without metadata.", mediaPath);
                log?.Report($"Could not parse metadata for {Path.GetFileName(mediaPath)}: {ex.Message}");
                item = new SourceMediaItem { FilePath = mediaPath, Connector = Name };
            }

            await onItem(item, ct).ConfigureAwait(false);
            emitted++;
        }
        return emitted;
    }

    /// <summary>
    /// Rewrite gallery-dl's download archive so it contains exactly the items the library tracks. The
    /// archive is gallery-dl's own SQLite file (<c>archive(entry TEXT PRIMARY KEY)</c>); for Pinterest an
    /// entry is the category followed by <c>{board_id}_{pin_id}</c> (e.g. <c>pinterest123_456</c>). By
    /// regenerating it from the DB each run, a deleted item stays skipped and the archive can never claim
    /// to have something the project doesn't (the drift risk of a second, independent list).
    /// </summary>
    private void RegenerateArchive(string archivePath, IReadOnlyCollection<KnownSourceItem> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = archivePath,
                Pooling = false,
            }.ToString());
            conn.Open();

            using var tx = conn.BeginTransaction();
            Execute(conn, tx, "CREATE TABLE IF NOT EXISTS archive (entry TEXT PRIMARY KEY) WITHOUT ROWID;");
            Execute(conn, tx, "DELETE FROM archive;");

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO archive (entry) VALUES ($e);";
                var p = insert.CreateParameter();
                p.ParameterName = "$e";
                insert.Parameters.Add(p);
                foreach (var item in items)
                {
                    p.Value = $"{Name}{item.BoardId}_{item.SourceId}";
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
            _logger.LogInformation("Rebuilt download archive at {Path} with {Count} entries.", archivePath, items.Count);
        }
        catch (Exception ex)
        {
            // A bad archive only costs re-downloads (dedup + tombstone checks keep the DB correct), so
            // never let archive maintenance break an import.
            _logger.LogWarning(ex, "Could not rebuild download archive at {Path}; continuing.", archivePath);
        }
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    [GeneratedRegex(@"\.(f\d+|faudio[\w-]*|fdash[\w-]*)\.(mp4|webm|m4a|m4v)$", RegexOptions.IgnoreCase)]
    private static partial Regex YtdlFragmentRegex();
}
