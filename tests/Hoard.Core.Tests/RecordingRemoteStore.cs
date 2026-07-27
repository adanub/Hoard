using Hoard.Core.Sync;

namespace Hoard.Core.Tests;

/// <summary>
/// A counting/faulting decorator over the real <see cref="FileSystemRemoteStore"/>. Deliberately a
/// decorator rather than an in-memory reimplementation: the atomic-rename and <c>.tmp-</c>-invisibility
/// semantics several replication rules lean on are the filesystem store's, and a hand-written double
/// would drift from them. What it adds is observability (how many round trips a sync really costs — the
/// whole point of delta mode) and injectable transfer failures.
/// </summary>
public sealed class RecordingRemoteStore(FileSystemRemoteStore inner) : IRemoteStore
{
    public string Root => inner.Root;

    public List<string> ListedPrefixes { get; } = [];
    public List<string> Heads { get; } = [];
    public List<string> Uploads { get; } = [];
    public List<string> Downloads { get; } = [];
    public int TotalCalls => ListedPrefixes.Count + Heads.Count + Uploads.Count + Downloads.Count + MarkerReads;
    public int MarkerReads { get; private set; }

    /// <summary>Paths matching this fail to upload, as a flaky share would.</summary>
    public Func<string, bool>? FailUpload { get; set; }

    /// <summary>Paths matching this fail to download.</summary>
    public Func<string, bool>? FailDownload { get; set; }


    /// <summary>Runs once, just before the first upload of a run — the seam for "the archive changed
    /// underneath us mid-sync".</summary>
    public Action? BeforeFirstUpload { get; set; }

    private bool _uploaded;

    public void Reset()
    {
        ListedPrefixes.Clear();
        Heads.Clear();
        Uploads.Clear();
        Downloads.Clear();
        MarkerReads = 0;
        _uploaded = false;
    }

    public Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken ct = default)
    {
        ListedPrefixes.Add(prefix);
        return inner.ListAsync(prefix, ct);
    }

    public Task<long?> GetLengthAsync(string relativePath, CancellationToken ct = default)
    {
        Heads.Add(relativePath);
        return inner.GetLengthAsync(relativePath, ct);
    }

    public Task DownloadAsync(string relativePath, string localFile, CancellationToken ct = default)
    {
        if (FailDownload?.Invoke(relativePath) == true) throw new IOException("download refused by the test");
        Downloads.Add(relativePath);
        return inner.DownloadAsync(relativePath, localFile, ct);
    }

    public Task UploadAsync(string relativePath, string localFile, CancellationToken ct = default)
    {
        if (!_uploaded)
        {
            _uploaded = true;
            BeforeFirstUpload?.Invoke();
        }
        if (FailUpload?.Invoke(relativePath) == true) throw new IOException("upload refused by the test");
        Uploads.Add(relativePath);
        return inner.UploadAsync(relativePath, localFile, ct);
    }

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        MarkerReads++;
        return inner.ReadTextAsync(relativePath, ct);
    }

    public Task WriteTextAsync(string relativePath, string text, CancellationToken ct = default)
        => inner.WriteTextAsync(relativePath, text, ct);
}
