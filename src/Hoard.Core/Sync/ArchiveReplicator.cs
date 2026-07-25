using System.Text.Json;
using Hoard.Core.Projects;

namespace Hoard.Core.Sync;

public sealed record ReplicationReport(int BlobsPushed, int ChaptersPushed, int BlobsPulled, int ChaptersPulled)
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
/// </summary>
public static class ArchiveReplicator
{
    public static async Task<ReplicationReport> PushAsync(
        HoardProject project, IRemoteStore remote, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureSameArchiveAsync(project, remote, seedIfEmpty: true, ct).ConfigureAwait(false);

        var remoteBlobs = (await remote.ListAsync("store/", ct).ConfigureAwait(false))
            .Select(o => o.RelativePath).ToHashSet(StringComparer.Ordinal);
        var blobs = 0;
        foreach (var (absolute, relative) in LocalBlobs(project))
        {
            ct.ThrowIfCancellationRequested();
            if (remoteBlobs.Contains(relative)) continue; // content-addressed: same path = same bytes
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
        foreach (var deviceId in ArchiveSegments.ListDevices(project.OpsRoot))
        {
            foreach (var (path, _) in ArchiveSegments.ListChapters(project.OpsRoot, deviceId))
            {
                ct.ThrowIfCancellationRequested();
                var relative = "ops/" + Path.GetFileName(path);
                // Re-stat the remote at the last moment, NOT from a start-of-run snapshot: two machines
                // pushing concurrently could otherwise regress a chapter (B lists, A uploads its newer
                // copy, B then overwrites it with a stale one). The remaining race is the copy itself —
                // a true conditional put arrives with the S3 store (R3).
                var remoteLength = await remote.GetLengthAsync(relative, ct).ConfigureAwait(false);
                if (remoteLength >= new FileInfo(path).Length) continue; // longer copy wins
                progress?.Report("Uploading history…");
                try
                {
                    await remote.UploadAsync(relative, path, ct).ConfigureAwait(false);
                    chapters++;
                }
                catch (IOException)
                {
                    // The active chapter is being appended this instant (another machine's import flush
                    // when the remote is itself a live archive folder) — skip it; the next sync re-lands
                    // it, and a chapter is never partially replaced (uploads are atomic).
                    progress?.Report("History file busy — it will sync next time.");
                }
            }
        }

        return new ReplicationReport(blobs, chapters, 0, 0);
    }

    public static async Task<ReplicationReport> PullAsync(
        HoardProject project, IRemoteStore remote, string? localDeviceId = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureSameArchiveAsync(project, remote, seedIfEmpty: false, ct).ConfigureAwait(false);

        // Blobs first (see the class doc): an op referencing a blob must find it already local.
        var blobs = 0;
        var localBlobs = LocalBlobs(project).Select(b => b.Relative).ToHashSet(StringComparer.Ordinal);
        foreach (var obj in await remote.ListAsync("store/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (localBlobs.Contains(obj.RelativePath)) continue;
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
        foreach (var obj in await remote.ListAsync("ops/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalPath(project, obj.RelativePath);
            // OUR OWN chapters are pulled only to bootstrap (local copy absent — a wiped folder). Once a
            // local copy exists it is authoritative regardless of length: one writer per device, and a
            // LONGER remote copy can still be stale (a pushed torn tail the local writer has since
            // repaired past) — replacing the file under the live ArchiveLog would silently drop ops its
            // flush watermark already counts as on disk.
            var isOwn = localDeviceId is not null
                        && ArchiveSegments.SegmentDevice(Path.GetFileName(obj.RelativePath)) == localDeviceId;
            if (File.Exists(local) && (isOwn || new FileInfo(local).Length >= obj.Length)) continue;
            progress?.Report("Downloading history…");
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

        return new ReplicationReport(0, 0, blobs, chapters);
    }

    /// <summary>
    /// Refuse to mix archives: the remote's marker must carry the SAME ProjectId as the local project.
    /// An empty remote (no marker) is seeded from the local marker on push; pull has nothing to take.
    /// A present-but-unreadable remote marker always refuses — never guess about someone's backup.
    /// </summary>
    private static async Task EnsureSameArchiveAsync(HoardProject project, IRemoteStore remote, bool seedIfEmpty, CancellationToken ct)
    {
        var text = await remote.ReadTextAsync(HoardProject.MarkerFileName, ct).ConfigureAwait(false);
        if (text is null)
        {
            if (!seedIfEmpty)
                throw new InvalidOperationException("The remote holds no archive yet — push to it first.");
            await remote.WriteTextAsync(
                HoardProject.MarkerFileName,
                await File.ReadAllTextAsync(project.MarkerPath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
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
    }

    private static IEnumerable<(string Absolute, string Relative)> LocalBlobs(HoardProject project)
    {
        if (!Directory.Exists(project.StoreRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(project.StoreRoot, "*", SearchOption.AllDirectories))
        {
            // A crash-orphaned ingest staging temp is machine-local junk, never archive content.
            if (Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal)) continue;
            yield return (file, "store/" + Path.GetRelativePath(project.StoreRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private static string LocalPath(HoardProject project, string relativePath)
        => Path.Combine(project.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
