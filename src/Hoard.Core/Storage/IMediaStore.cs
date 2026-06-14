namespace Hoard.Core.Storage;

/// <summary>Result of putting a file into the content-addressed store.</summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the file's bytes.</param>
/// <param name="RelativePath">Path of the blob relative to the store root.</param>
/// <param name="Bytes">File size in bytes.</param>
/// <param name="AlreadyExisted">True if a blob with this hash was already present (dedupe hit).</param>
public readonly record struct StoredBlob(string Sha256, string RelativePath, long Bytes, bool AlreadyExisted);

public interface IMediaStore
{
    /// <summary>Copy a source file into the store, addressed by the SHA-256 of its contents.</summary>
    Task<StoredBlob> PutAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>Absolute path of a blob given its store-relative path.</summary>
    string GetAbsolutePath(string relativePath);

    bool Exists(string sha256, string extension);
}
