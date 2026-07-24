using System.Linq;
using System.Security.Cryptography;

namespace Hoard.Core.Storage;

/// <summary>
/// Stores blobs on disk addressed by the SHA-256 of their contents, sharded two levels deep to
/// keep directories small: <c>&lt;root&gt;/ab/cd/&lt;sha256&gt;.&lt;ext&gt;</c>. Originals are
/// immutable; writing a blob that already exists is a no-op, which gives free cross-board dedup.
/// </summary>
public sealed class ContentAddressedStore : IMediaStore
{
    private readonly string _root;

    public ContentAddressedStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<StoredBlob> PutAsync(string sourcePath, CancellationToken ct = default)
    {
        var sha = await ComputeSha256Async(sourcePath, ct).ConfigureAwait(false);
        // Preserve the extension so the store is browsable and MIME sniffing stays cheap.
        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        var relativePath = BuildRelativePath(sha, ext);
        var absolutePath = Path.Combine(_root, relativePath);

        if (File.Exists(absolutePath))
        {
            return new StoredBlob(sha, relativePath, new FileInfo(absolutePath).Length, AlreadyExisted: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        // Copy (not move): the caller owns the temp download dir and cleans it up itself.
        File.Copy(sourcePath, absolutePath, overwrite: false);
        return new StoredBlob(sha, relativePath, new FileInfo(absolutePath).Length, AlreadyExisted: false);
    }

    public string GetAbsolutePath(string relativePath) => Path.Combine(_root, Normalise(relativePath));

    public bool Exists(string sha256, string extension)
        => File.Exists(GetAbsolutePath(BuildRelativePath(sha256, extension.TrimStart('.').ToLowerInvariant())));

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        var absolutePath = GetAbsolutePath(relativePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
            // Tidy now-empty shard directories so the store doesn't accumulate empty ab/cd folders.
            PruneEmptyParents(Path.GetDirectoryName(absolutePath));
        }
        return Task.CompletedTask;
    }

    public void PruneEmptyShards(IEnumerable<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
            PruneEmptyParents(Path.GetDirectoryName(GetAbsolutePath(relativePath)));
    }

    /// <summary>Remove empty parent directories up to (but not including) the store root.</summary>
    private void PruneEmptyParents(string? directory)
    {
        var root = Path.GetFullPath(_root);
        while (directory is not null
               && !string.Equals(Path.GetFullPath(directory), root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    // Relative paths are persisted to the project DB, and a project folder must open on any OS — so the
    // stored form is canonically '/'-separated (never Path.Combine, whose separator is the current OS's).
    private static string BuildRelativePath(string sha, string ext)
    {
        var name = string.IsNullOrEmpty(ext) ? sha : $"{sha}.{ext}";
        return $"{sha[..2]}/{sha[2..4]}/{name}";
    }

    // DBs written by Windows builds before the canonical form hold '\'-separated paths; resolve either
    // separator to the current OS's so those projects keep opening everywhere.
    private static string Normalise(string relativePath)
        => relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
