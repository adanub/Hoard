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

    public string GetAbsolutePath(string relativePath) => Path.Combine(_root, relativePath);

    public bool Exists(string sha256, string extension)
        => File.Exists(Path.Combine(_root, BuildRelativePath(sha256, extension.TrimStart('.').ToLowerInvariant())));

    private static string BuildRelativePath(string sha, string ext)
    {
        var name = string.IsNullOrEmpty(ext) ? sha : $"{sha}.{ext}";
        return Path.Combine(sha[..2], sha[2..4], name);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
