using System.Text.Json;
using Hoard.Core.Domain;
using Hoard.Core.Projects;

namespace Hoard.Core.Sync;

/// <summary>How much of the file set a replication run reconciles.</summary>
public enum ReplicationMode
{
    /// <summary>Move only what the op log proves is new — the every-run path. Costs a marker read, one
    /// <c>ops/</c> listing, and a couple of stats per chapter, regardless of how big the archive is.</summary>
    Delta,

    /// <summary>Reconcile the WHOLE file set both ways (the "Repair backup" action): re-list every blob on
    /// both sides and move whatever is missing or the wrong length. The only path that heals damage the op
    /// log can't describe — a remote someone deleted files from, or blobs no op names.</summary>
    Full,
}

/// <summary>
/// What one replication run moved. <see cref="ChaptersDeferred"/> counts chapters deliberately left for
/// the next run (a blob they need wouldn't transfer, or the file was busy) — the archive stays consistent,
/// it just isn't finished. <see cref="BlobsUnavailable"/> counts blobs an op names that the REMOTE turned
/// out not to hold: the honest "your backup is incomplete" signal.
/// </summary>
public sealed record ReplicationReport(
    int BlobsPushed, int ChaptersPushed, int BlobsPulled, int ChaptersPulled,
    int ChaptersDeferred = 0, int BlobsUnavailable = 0, bool Verified = false)
{
    public bool AnythingMoved => BlobsPushed + ChaptersPushed + BlobsPulled + ChaptersPulled > 0;
}

/// <summary>
/// P5/R1: replicate the archive between its local folder and an <see cref="IRemoteStore"/>. Because the
/// archive is immutable/append-only files, sync is pure file-set reconciliation — no protocol, no
/// conflict resolution beyond two rules:
///  - <b>blobs move before segments</b>, in both directions, so op-implies-blob holds on whichever side
///    just received ops (an interrupted transfer leaves spare blobs, never dangling ops);
///  - <b>segments converge by length</b>: a chapter file only ever grows (closed chapters are
///    equal-or-absent; only the active chapter differs), so the longer copy wins and equal length means
///    identical.
/// Only the archive proper moves (marker + <c>store/</c> + <c>ops/</c>) — never migration backups,
/// derived caches, or strays. The remote's marker is verified to be the SAME archive before a single
/// byte moves; push seeds an empty remote with the local marker, pull refuses one. Pull never deletes
/// local state — removals propagate through ops at the next open's catch-up, not through file sync.
/// Run at open/import boundaries (the R2 UI gates it), not concurrently with an import's own writes.
/// <para><b>Delta mode (the default) makes the op log the cursor.</b> A chapter is append-only with one
/// writer, so the remote's byte length of it says exactly which ops it already holds — and the ops past
/// that offset name exactly the blobs it still lacks (<see cref="ArchiveOpBlobs"/>). That replaces both
/// full store listings with a couple of stats per chapter, so a sync costs what CHANGED rather than what
/// the archive contains. The load-bearing consequence: <b>a chapter is the receipt</b>. Publishing it
/// moves the cursor permanently, so it may only be published once every blob its new ops name has landed
/// — and the bytes published must be exactly the bytes scanned, which is why both directions stage a
/// frozen copy first. What delta CANNOT see (blobs no op names, files deleted from the remote behind our
/// back) is what <see cref="ReplicationMode.Full"/> exists for.</para>
/// </summary>
public static class ArchiveReplicator
{
    /// <summary>Staging names carry this marker so every layer agrees they are invisible in-flight state:
    /// segment listings glob <c>*.jsonl</c> and never match, and remote listings filter it out.</summary>
    private const string StagingMarker = ".tmp-";

    /// <summary>A crash-orphaned staging file older than this is swept at the start of a pull.</summary>
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromHours(1);

    public static async Task<ReplicationReport> PushAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        ReplicationMode mode = ReplicationMode.Delta,
        IProgress<string>? progress = null, CancellationToken ct = default, bool markerVerified = false)
    {
        if (!markerVerified)
            await EnsureSameArchiveAsync(project, remote, seedIfEmpty: true, ct).ConfigureAwait(false);

        return mode == ReplicationMode.Full
            ? await PushFullAsync(project, remote, localDeviceId, progress, ct).ConfigureAwait(false)
            : await PushDeltaAsync(project, remote, localDeviceId, progress, ct).ConfigureAwait(false);
    }

    public static async Task<ReplicationReport> PullAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        ReplicationMode mode = ReplicationMode.Delta,
        IProgress<string>? progress = null, CancellationToken ct = default, bool markerVerified = false)
    {
        if (!markerVerified)
            await EnsureSameArchiveAsync(project, remote, seedIfEmpty: false, ct).ConfigureAwait(false);

        return mode == ReplicationMode.Full
            ? await PullFullAsync(project, remote, localDeviceId, progress, ct).ConfigureAwait(false)
            : await PullDeltaAsync(project, remote, localDeviceId, progress, ct).ConfigureAwait(false);
    }

    // ── delta push ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One chapter the remote is behind on, frozen at the length we reasoned about.</summary>
    private sealed record PendingChapter(string Relative, string Frozen, long Length, bool IsOwn, List<string> Blobs);

    private static async Task<ReplicationReport> PushDeltaAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking the backup…");
        var pending = new List<PendingChapter>();
        var candidates = new Dictionary<string, BlobRef>(StringComparer.Ordinal);
        var deferred = 0;

        try
        {
            foreach (var deviceId in ArchiveSegments.ListDevices(project.OpsRoot))
            {
                foreach (var (path, _) in ArchiveSegments.ListChapters(project.OpsRoot, deviceId))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = ArchiveSegments.DirectoryName + "/" + Path.GetFileName(path);
                    var remoteLength = await remote.GetLengthAsync(relative, ct).ConfigureAwait(false);
                    var isOwn = deviceId == localDeviceId;

                    // Decide from the ORIGINAL (one short tail read), because the overwhelmingly common
                    // answer is "nothing to send" and copying every chapter to find that out would put
                    // the whole ops history back on the wire each run — chapter zero of a long-lived
                    // archive predates rotation and can be tens of megabytes.
                    // Our OWN chapter is authoritative: push whenever the remote differs, including when
                    // the remote copy is LONGER (a torn tail an older build pushed, which we have since
                    // repaired past — comparing with >= would wedge that chapter forever). A foreign
                    // chapter we merely relay, so the longer copy still wins.
                    if (isOwn ? remoteLength == ArchiveSegments.ValidLength(path)
                              : remoteLength >= ArchiveSegments.ValidLength(path)) continue;

                    // Only now FREEZE the bytes. UploadAsync copies the file as it is at upload time, and
                    // the active chapter can grow while we spend minutes uploading blobs — the remote
                    // would then hold ops whose blobs were never scanned, and its new length would mark
                    // them "already backed up" forever. Truncating to the whole-line prefix as we go also
                    // keeps every length we ever publish on a line boundary.
                    var frozen = FreezeChapter(path, out var length);
                    if (frozen is null)
                    {
                        // Being appended this instant — leave it; the next sync re-lands it.
                        progress?.Report("History file busy — it will sync next time.");
                        deferred++;
                        continue;
                    }
                    // Re-test against the frozen copy: the decision above was taken a moment ago and the
                    // file could have been repaired shorter in between.
                    if (isOwn ? remoteLength == length : remoteLength >= length)
                    {
                        TryDelete(frozen);
                        continue;
                    }

                    // Ops the remote already holds need no blobs — it got them when it took those ops.
                    // An out-of-range offset (a longer, stale remote copy) falls back to the whole chapter.
                    var offset = remoteLength is { } known && known <= length ? known : 0;
                    var blobs = new List<string>();
                    foreach (var op in ArchiveSegments.ReadFrom(frozen, deviceId, offset))
                    {
                        if (ResolveBlob(project, op) is not { } blob) continue;
                        candidates.TryAdd(blob.RemoteKey, blob);
                        blobs.Add(blob.RemoteKey);
                    }
                    pending.Add(new PendingChapter(relative, frozen, length, isOwn, blobs));
                }
            }

            var settled = await UploadBlobsAsync(remote, candidates, progress, ct).ConfigureAwait(false);
            var chapters = 0;
            foreach (var chapter in pending)
            {
                ct.ThrowIfCancellationRequested();
                // The chapter IS the receipt — publishing it tells every future run "everything below
                // this offset is backed up". One blob that wouldn't transfer means we say that later.
                if (chapter.Blobs.Any(key => !settled.Contains(key)))
                {
                    deferred++;
                    continue;
                }

                // Re-stat at the last moment, NOT from the planning stat above: two machines pushing
                // concurrently could otherwise regress a chapter (B plans, A uploads its newer copy, B
                // then overwrites it with a stale one). The remaining race is the copy itself.
                var remoteLength = await remote.GetLengthAsync(chapter.Relative, ct).ConfigureAwait(false);
                if (chapter.IsOwn ? remoteLength == chapter.Length : remoteLength >= chapter.Length) continue;

                progress?.Report("Saving history…");
                try
                {
                    await remote.UploadAsync(chapter.Relative, chapter.Frozen, ct).ConfigureAwait(false);
                    chapters++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    deferred++;
                }
            }

            return new ReplicationReport(settled.UploadedCount, chapters, 0, 0, deferred);
        }
        finally
        {
            foreach (var chapter in pending) TryDelete(chapter.Frozen);
        }
    }

    /// <summary>Copy a chapter's whole-line prefix aside, so the bytes we scan are the bytes we publish.
    /// Null when the live writer holds it (a flush this instant) — that chapter waits for the next run.</summary>
    private static string? FreezeChapter(string path, out long length)
    {
        length = 0;
        var frozen = path + StagingMarker + "push-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(path, frozen);
            length = ArchiveSegments.ValidLength(frozen);
            using (var stream = new FileStream(frozen, FileMode.Open, FileAccess.Write, FileShare.None))
                stream.SetLength(length);
            return frozen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(frozen);
            return null;
        }
    }

    /// <summary>Which of a run's blob candidates the remote is known to hold once we're done.</summary>
    private sealed class SettledBlobs
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        public int UploadedCount { get; set; }
        public bool Contains(string key) => _keys.Contains(key);
        public void Add(string key) => _keys.Add(key);
    }

    private static async Task<SettledBlobs> UploadBlobsAsync(
        IRemoteStore remote, Dictionary<string, BlobRef> candidates, IProgress<string>? progress, CancellationToken ct)
    {
        var settled = new SettledBlobs();
        var done = 0;
        foreach (var (key, blob) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            // A re-emitted asset.added (a title edit, a board move) names a blob the remote already has;
            // comparing the payload's own size means a re-crawl of thousands of unchanged pins costs a
            // stat each instead of a re-upload each — and a length MISMATCH repairs a torn remote copy.
            var remoteLength = await remote.GetLengthAsync(key, ct).ConfigureAwait(false);
            if (remoteLength is { } length && (blob.Bytes < 0 || length == blob.Bytes))
            {
                settled.Add(key);
                continue;
            }

            if (!File.Exists(blob.LocalPath))
            {
                // Freed by a tombstone since the op was written — genuinely nothing to move, and the
                // chapter must not wait forever on it.
                settled.Add(key);
                continue;
            }

            progress?.Report($"Uploading image {done} of {candidates.Count}…");
            try
            {
                await remote.UploadAsync(key, blob.LocalPath, ct).ConfigureAwait(false);
                settled.Add(key);
                settled.UploadedCount++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                settled.Add(key); // vanished mid-run — same as above
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left unsettled on purpose: its chapter stays unpublished and the next run retries.
            }
        }
        return settled;
    }

    // ── delta pull ───────────────────────────────────────────────────────────────────────────────

    private static async Task<ReplicationReport> PullDeltaAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        IProgress<string>? progress, CancellationToken ct)
    {
        SweepStaleStaging(project.OpsRoot);
        int blobs = 0, chapters = 0, deferred = 0, unavailable = 0;
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var obj in await remote.ListAsync(ArchiveSegments.DirectoryName + "/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (ArchiveSegments.SegmentDevice(Path.GetFileName(obj.RelativePath)) is not { } deviceId)
                continue; // a stray in the remote's ops folder — not a segment, nothing to apply

            var local = LocalPath(project, obj.RelativePath);
            var exists = File.Exists(local);
            // OUR OWN chapters are pulled only to bootstrap (local copy absent — a wiped folder). Once a
            // local copy exists it is authoritative regardless of length: one writer per device, and a
            // LONGER remote copy can still be stale (a pushed torn tail the local writer has since
            // repaired past) — replacing the file under the live ArchiveLog would silently drop ops its
            // flush watermark already counts as on disk.
            if (exists && deviceId == localDeviceId) continue;
            // "Do I need this?" compares RAW length against raw length — like for like. Measuring the
            // local side by its whole-line prefix instead would never converge on a remote copy carrying
            // a torn tail: taking it makes the local file exactly as long, yet its valid prefix stays
            // shorter, so every future sync would re-fetch and re-apply the same chapter forever.
            var localLength = exists ? new FileInfo(local).Length : 0;
            if (exists && obj.Length <= localLength) continue;
            // The READ offset is the whole-line prefix, though: a torn trailing line is not an op, and
            // starting the scan mid-line would be nonsense (ReadFrom re-reads from 0 if it isn't aligned).
            var readFrom = exists ? ArchiveSegments.ValidLength(local) : 0;

            var staged = local + StagingMarker + Guid.NewGuid().ToString("N");
            try
            {
                await remote.DownloadAsync(obj.RelativePath, staged, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // vanished between listing and download — the next sync sees the real state
            }

            try
            {
                // Blobs first (see the class doc): the ops arriving in this chapter must find their blobs
                // already local, so the staged file is only moved into place once they have.
                var settled = true;
                foreach (var op in ArchiveSegments.ReadFrom(staged, deviceId, readFrom))
                {
                    ct.ThrowIfCancellationRequested();
                    if (ResolveBlob(project, op) is not { } blob) continue;
                    // "handled" means SETTLED, never merely "seen": a blob whose transfer failed must stay
                    // outstanding, or a later chapter naming the same blob would skip it as done, find
                    // nothing else pending, and publish itself — leaving ops whose image never arrived,
                    // at a length no future delta pull would ever look below.
                    if (handled.Contains(blob.RemoteKey)) continue;
                    if (File.Exists(blob.LocalPath)) { handled.Add(blob.RemoteKey); continue; }

                    progress?.Report($"Downloading image {blobs + 1}…");
                    try
                    {
                        await remote.DownloadAsync(blob.RemoteKey, blob.LocalPath, ct).ConfigureAwait(false);
                        blobs++;
                        handled.Add(blob.RemoteKey);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // The remote names a blob it doesn't hold — an incomplete backup, not our bug.
                        // Settled (there is nothing to fetch, this run or any other) so it can't wedge the
                        // chapter; counted so the UI can say the backup is short.
                        unavailable++;
                        handled.Add(blob.RemoteKey);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        settled = false;
                        break; // a transfer failure — leave the whole chapter for the next run
                    }
                }

                if (!settled)
                {
                    deferred++;
                    continue;
                }

                progress?.Report("Saving history…");
                Directory.CreateDirectory(project.OpsRoot);
                File.Move(staged, local, overwrite: true);
                chapters++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deferred++;
            }
            finally
            {
                TryDelete(staged);
            }
        }

        return new ReplicationReport(0, 0, blobs, chapters, deferred, unavailable);
    }

    /// <summary>Drop staging files a crashed run left in <c>ops/</c>. They are already invisible to every
    /// reader (segment listings glob <c>*.jsonl</c>); this just stops them accumulating.</summary>
    private static void SweepStaleStaging(string opsRoot)
    {
        if (!Directory.Exists(opsRoot)) return;
        try
        {
            var cutoff = DateTime.UtcNow - StagingLifetime;
            foreach (var file in Directory.EnumerateFiles(opsRoot, "*" + StagingMarker + "*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tidying is best-effort; a stray staging file harms nothing.
        }
    }

    // ── full (repair) push/pull — reconcile the whole file set ───────────────────────────────────

    private static async Task<ReplicationReport> PushFullAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking every file in the backup…");
        var remoteBlobs = (await remote.ListAsync("store/", ct).ConfigureAwait(false))
            .ToDictionary(o => o.RelativePath, o => o.Length, StringComparer.Ordinal);
        var blobs = 0;
        foreach (var (absolute, relative) in LocalBlobs(project))
        {
            ct.ThrowIfCancellationRequested();
            // Content-addressed, so the same path is the same bytes — but only at the same LENGTH: a
            // remote copy torn by an interrupted upload sits at the right address with the wrong size.
            if (remoteBlobs.TryGetValue(relative, out var remoteLength)
                && remoteLength == new FileInfo(absolute).Length) continue;
            progress?.Report($"Uploading image {blobs + 1}…");
            try
            {
                await remote.UploadAsync(relative, absolute, ct).ConfigureAwait(false);
                blobs++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The blob vanished mid-run — a delete/tombstone freed it after enumeration. Correctly
                // not replicated; carry on rather than abort everything not yet uploaded.
            }
        }

        var chapters = 0;
        var deferred = 0;
        foreach (var deviceId in ArchiveSegments.ListDevices(project.OpsRoot))
        {
            foreach (var (path, _) in ArchiveSegments.ListChapters(project.OpsRoot, deviceId))
            {
                ct.ThrowIfCancellationRequested();
                var relative = ArchiveSegments.DirectoryName + "/" + Path.GetFileName(path);
                var remoteLength = await remote.GetLengthAsync(relative, ct).ConfigureAwait(false);
                var isOwn = deviceId == localDeviceId;
                if (isOwn ? remoteLength == ArchiveSegments.ValidLength(path)
                          : remoteLength >= ArchiveSegments.ValidLength(path)) continue;

                // Repair publishes the frozen whole-line prefix, exactly as delta does. Uploading the live
                // file would let a local torn tail become the remote's length — and every OTHER device
                // then measures itself against bytes that aren't ops. It is also why our own chapter uses
                // "differs" rather than "is shorter": Repair has to be able to replace a torn remote copy
                // with the repaired one, which can legitimately be shorter.
                var frozen = FreezeChapter(path, out _);
                if (frozen is null)
                {
                    progress?.Report("History file busy — it will sync next time.");
                    deferred++;
                    continue;
                }
                progress?.Report("Saving history…");
                try
                {
                    await remote.UploadAsync(relative, frozen, ct).ConfigureAwait(false);
                    chapters++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The active chapter is being appended this instant — skip it; the next sync re-lands
                    // it, and a chapter is never partially replaced (uploads are atomic).
                    progress?.Report("History file busy — it will sync next time.");
                    deferred++;
                }
                finally
                {
                    TryDelete(frozen);
                }
            }
        }

        return new ReplicationReport(blobs, chapters, 0, 0, deferred, 0, Verified: true);
    }

    private static async Task<ReplicationReport> PullFullAsync(
        HoardProject project, IRemoteStore remote, string localDeviceId,
        IProgress<string>? progress, CancellationToken ct)
    {
        // Blobs first (see the class doc): an op referencing a blob must find it already local.
        var blobs = 0;
        var localBlobs = LocalBlobs(project).ToDictionary(b => b.Relative, b => b.Absolute, StringComparer.Ordinal);
        foreach (var obj in await remote.ListAsync("store/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (localBlobs.TryGetValue(obj.RelativePath, out var absolute)
                && new FileInfo(absolute).Length == obj.Length) continue;
            progress?.Report($"Downloading image {blobs + 1}…");
            try
            {
                await remote.DownloadAsync(obj.RelativePath, LocalPath(project, obj.RelativePath), ct).ConfigureAwait(false);
                blobs++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone between listing and download (a rare remote-side tidy) — nothing to take.
            }
        }

        var chapters = 0;
        foreach (var obj in await remote.ListAsync(ArchiveSegments.DirectoryName + "/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalPath(project, obj.RelativePath);
            var isOwn = ArchiveSegments.SegmentDevice(Path.GetFileName(obj.RelativePath)) == localDeviceId;
            // Raw against raw, like the delta pull: measuring the local side by its whole-line prefix
            // would re-fetch a torn remote chapter on every single run without ever converging.
            if (File.Exists(local) && (isOwn || new FileInfo(local).Length >= obj.Length)) continue;
            progress?.Report("Saving history…");
            try
            {
                await remote.DownloadAsync(obj.RelativePath, local, ct).ConfigureAwait(false);
                chapters++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Vanished between listing and download — the next sync sees the remote's real state.
            }
        }

        return new ReplicationReport(0, 0, blobs, chapters, 0, 0, Verified: true);
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuse to mix archives: the remote's marker must carry the SAME ProjectId as the local project.
    /// An empty remote (no marker) is seeded from the local marker on push; pull has nothing to take.
    /// A present-but-unreadable remote marker always refuses — never guess about someone's backup.
    /// Returns true when the remote already held an archive (false = we just seeded it).
    /// </summary>
    public static async Task<bool> EnsureSameArchiveAsync(
        HoardProject project, IRemoteStore remote, bool seedIfEmpty, CancellationToken ct = default)
    {
        var text = await remote.ReadTextAsync(HoardProject.MarkerFileName, ct).ConfigureAwait(false);
        if (text is null)
        {
            if (!seedIfEmpty)
                throw new InvalidOperationException("The remote holds no archive yet — push to it first.");
            await remote.WriteTextAsync(
                HoardProject.MarkerFileName,
                await File.ReadAllTextAsync(project.MarkerPath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return false;
        }

        Guid remoteId;
        try
        {
            using var marker = JsonDocument.Parse(text);
            remoteId = marker.RootElement.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var parsed)
                ? parsed
                : default;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not only malformed JSON: a marker that is valid JSON but the wrong SHAPE ('null', an
            // array, a numeric id) throws InvalidOperationException from TryGetProperty/GetString — the
            // same honest refusal applies, never a raw framework message.
            throw new InvalidOperationException("The remote's archive marker is unreadable — refusing to sync with it.");
        }
        if (remoteId != project.Id)
            throw new InvalidOperationException("The remote holds a DIFFERENT archive — refusing to mix two projects.");
        return true;
    }

    /// <summary>A blob an op names, resolved to a remote key and a local path that is PROVEN to sit
    /// inside the store. Payload paths are archive content, so they can be malformed or hostile: a
    /// rooted or <c>..</c>-bearing path would otherwise let a pull write anywhere on disk.</summary>
    private readonly record struct BlobRef(string RemoteKey, string LocalPath, long Bytes);

    private static BlobRef? ResolveBlob(HoardProject project, ArchiveOp op)
    {
        if (ArchiveOpBlobs.Referenced(op) is not { } reference) return null;

        // DBs written by Windows builds before the canonical form hold '\'-separated paths, so the same
        // blob must resolve to the same remote key either way.
        var relative = reference.RelativePath.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0 || Path.IsPathRooted(relative)) return null;
        foreach (var segment in relative.Split('/'))
            if (segment is "" or "." or "..") return null;
        if (relative.Contains(StagingMarker, StringComparison.Ordinal)) return null;

        var storeRoot = Path.GetFullPath(project.StoreRoot);
        var local = Path.GetFullPath(Path.Combine(storeRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = storeRoot.EndsWith(Path.DirectorySeparatorChar) ? storeRoot : storeRoot + Path.DirectorySeparatorChar;
        if (!local.StartsWith(prefix, StringComparison.Ordinal)) return null;

        return new BlobRef("store/" + relative, local, reference.Bytes);
    }

    private static IEnumerable<(string Absolute, string Relative)> LocalBlobs(HoardProject project)
    {
        if (!Directory.Exists(project.StoreRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(project.StoreRoot, "*", SearchOption.AllDirectories))
        {
            // A crash-orphaned ingest staging temp is machine-local junk, never archive content.
            if (Path.GetFileName(file).Contains(StagingMarker, StringComparison.Ordinal)) continue;
            yield return (file, "store/" + Path.GetRelativePath(project.StoreRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private static string LocalPath(HoardProject project, string relativePath)
        => Path.Combine(project.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a stray staging file harms nothing */ }
    }
}
