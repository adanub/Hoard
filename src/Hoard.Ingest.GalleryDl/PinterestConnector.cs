using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Hoard.Core.Connectors;
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

    public async Task DownloadAsync(
        string url, ConnectorOptions options, IProgress<string>? log,
        Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
    {
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
            psi.ArgumentList.Add("--write-metadata");           // emit <file>.json sidecars
            psi.ArgumentList.Add("--directory");                // flat output; we group by sidecar metadata, not path
            psi.ArgumentList.Add(tempDir);
            AddCookieArgs(psi, options);
            AddRateLimitArgs(psi, options.RateLimit);
            if (options.MaxItems is int max and > 0)
            {
                psi.ArgumentList.Add("--range");
                psi.ArgumentList.Add($"1-{max}");
            }
            if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath))
            {
                // Skip pins already recorded from a previous import, so re-runs only fetch what's new.
                psi.ArgumentList.Add("--download-archive");
                psi.ArgumentList.Add(options.DownloadArchivePath);
            }
            psi.ArgumentList.Add(url);

            _logger.LogInformation("Running gallery-dl for {Url} into {TempDir}", url, tempDir);

            // Keep a rolling tail of stderr so a failure can report *why*, not just a number.
            var errorTail = new Queue<string>();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogInformation("gallery-dl: {Line}", e.Data);
                log?.Report(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _logger.LogWarning("gallery-dl: {Line}", e.Data);
                log?.Report(e.Data);
                errorTail.Enqueue(e.Data);
                while (errorTail.Count > 10) errorTail.Dequeue();
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start gallery-dl at '{_galleryDlPath}'. Is it bundled/installed?", ex);
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

            _logger.LogInformation("gallery-dl exit {Code}; produced {Count} media items for {Url}",
                process.ExitCode, count, url);

            if (count > 0) return;

            // Nothing came back. Distinguish a real dead-end from "everything was already archived".
            var detail = errorTail.Count > 0 ? " " + string.Join(" | ", errorTail) : "";

            // A "not found" on a board that exists almost always means it's private and the request
            // was unauthenticated — point at the cookies, which is the real fix.
            var looksPrivate = errorTail.Any(l =>
                l.Contains("NotFoundError", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("could not be found", StringComparison.OrdinalIgnoreCase));
            if (looksPrivate)
                throw new InvalidOperationException(
                    "Pinterest returned \"board not found\". If the board is private, select the browser " +
                    "you're logged into Pinterest with in the Cookies dropdown (Firefox-based browsers " +
                    "like Zen are supported)." + detail);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"gallery-dl failed (exit {process.ExitCode}) and downloaded nothing.{detail}");

            // Clean exit, no new files: with an archive in use this just means the board is already
            // fully backed up — a normal, successful re-import, not an error.
            if (!string.IsNullOrWhiteSpace(options.DownloadArchivePath))
            {
                _logger.LogInformation("Nothing new for {Url}; already up to date.", url);
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

    private static void AddCookieArgs(ProcessStartInfo psi, ConnectorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CookiesFromBrowser))
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add(options.CookiesFromBrowser);
        }
        else if (!string.IsNullOrWhiteSpace(options.CookiesFile))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(options.CookiesFile);
        }
    }

    private static void AddRateLimitArgs(ProcessStartInfo psi, RateLimitOptions rate)
    {
        // Emit "min-max" when there's jitter so gallery-dl waits a random time in that range per item
        // (a less robotic cadence); a plain value otherwise.
        void AddSleep(string flag, double seconds, double jitter)
        {
            if (seconds <= 0 && jitter <= 0) return;
            var value = jitter > 0
                ? $"{seconds.ToString(CultureInfo.InvariantCulture)}-{(seconds + jitter).ToString(CultureInfo.InvariantCulture)}"
                : seconds.ToString(CultureInfo.InvariantCulture);
            psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add(value);
        }

        AddSleep("--sleep-request", rate.RequestIntervalSeconds, rate.RequestIntervalJitterSeconds);
        AddSleep("--sleep", rate.DownloadIntervalSeconds, rate.DownloadIntervalJitterSeconds);
        AddSleep("--sleep-429", rate.TooManyRequestsBackoffSeconds, jitter: 0);
        if (!string.IsNullOrWhiteSpace(rate.MaxRate))
        {
            psi.ArgumentList.Add("--limit-rate");
            psi.ArgumentList.Add(rate.MaxRate);
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

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    [GeneratedRegex(@"\.(f\d+|faudio[\w-]*|fdash[\w-]*)\.(mp4|webm|m4a|m4v)$", RegexOptions.IgnoreCase)]
    private static partial Regex YtdlFragmentRegex();
}
