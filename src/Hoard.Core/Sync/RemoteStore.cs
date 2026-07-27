namespace Hoard.Core.Sync;

/// <summary>One object held by a remote, addressed by its archive-relative '/'-separated path.</summary>
public sealed record RemoteObject(string RelativePath, long Length);

/// <summary>
/// A dumb object store holding a copy of the archive's files (<c>SYNC-DESIGN.md</c> P5/R0): list,
/// download, upload, and small-text read/write — nothing archive-aware. The archive format only ever
/// needs immutable objects (blobs, sealed chapters) plus atomic whole-object replace (the active
/// chapter, the marker), which every real backend offers — a mounted folder here, S3/B2 later.
/// Paths are archive-relative and '/'-separated (<c>store/ab/cd/…</c>, <c>ops/device.jsonl</c>).
/// </summary>
public interface IRemoteStore
{
    /// <summary>Objects whose path starts with <paramref name="prefix"/>. Missing prefix = empty.
    /// Never includes in-flight staging temps (<c>.tmp-</c> names) — a half-uploaded object must be
    /// invisible until its atomic publish.</summary>
    Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>One object's current length, or null when absent — the just-before-upload freshness
    /// check (a start-of-run listing goes stale under a concurrent pusher). Maps to HEAD on S3.</summary>
    Task<long?> GetLengthAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Fetch one object into <paramref name="localFile"/> (replaced atomically).</summary>
    Task DownloadAsync(string relativePath, string localFile, CancellationToken ct = default);

    /// <summary>Store one object from <paramref name="localFile"/>, replacing atomically if present.</summary>
    Task UploadAsync(string relativePath, string localFile, CancellationToken ct = default);

    /// <summary>Read a small text object (the marker), or null when absent.</summary>
    Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Write a small text object atomically.</summary>
    Task WriteTextAsync(string relativePath, string text, CancellationToken ct = default);
}

/// <summary>
/// An <see cref="IRemoteStore"/> over any mounted path — a backup drive, an rclone/Syncthing folder, a
/// second NAS share. Doubles as the replication engine's test harness and a real feature (push a
/// replica of the archive to a dumb folder). All writes are temp-file + rename, per the archive's
/// blob-write rule.
/// </summary>
public sealed class FileSystemRemoteStore : IRemoteStore
{
    private readonly string _root;

    public FileSystemRemoteStore(string root) => _root = Path.GetFullPath(root);

    public string Root => _root;

    public Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var dir = Local(prefix.TrimEnd('/'));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<RemoteObject>>([]);
        // Enumerate FileInfos, not paths: the entries the directory walk yields already carry their size,
        // so Length costs nothing. Re-stating each path (new FileInfo(f).Length) is a separate round trip
        // per file — thousands of them over SMB, which is most of what made a full listing slow.
        var objects = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
            // Staging temps are invisible (the interface contract): another machine's in-flight upload
            // (or a crash-orphaned one) must never be pulled as junk or abort a sync when its rename wins.
            .Where(f => !f.Name.Contains(".tmp-", StringComparison.Ordinal))
            .Select(f => new RemoteObject(
                Path.GetRelativePath(_root, f.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                f.Length))
            .ToList();
        return Task.FromResult<IReadOnlyList<RemoteObject>>(objects);
    }

    public Task<long?> GetLengthAsync(string relativePath, CancellationToken ct = default)
    {
        // One stat, not two: FileInfo answers both questions from the same lookup.
        var info = new FileInfo(Local(relativePath));
        return Task.FromResult(info.Exists ? info.Length : (long?)null);
    }

    public Task DownloadAsync(string relativePath, string localFile, CancellationToken ct = default)
        => CopyAtomic(Local(relativePath), localFile);

    public Task UploadAsync(string relativePath, string localFile, CancellationToken ct = default)
        => CopyAtomic(localFile, Local(relativePath));

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Local(relativePath);
        return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public async Task WriteTextAsync(string relativePath, string text, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "hoard-remote-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(temp, text, ct).ConfigureAwait(false);
        try
        {
            await CopyAtomic(temp, Local(relativePath)).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private string Local(string relativePath)
        => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static Task CopyAtomic(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var temp = to + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(from, temp);
            File.Move(temp, to, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        return Task.CompletedTask;
    }
}
