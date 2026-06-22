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

    /// <summary>
    /// Delete a blob from the store by its store-relative path. A no-op if it's already gone. Because an
    /// asset's SHA-256 is unique, one asset maps to exactly one blob, so deleting the asset's blob frees
    /// it outright (no shared-reference counting needed).
    /// </summary>
    Task DeleteAsync(string relativePath, CancellationToken ct = default);

    /// <summary>Remove any now-empty shard directories left behind after blobs were removed out-of-band — e.g.
    /// recycled in one batched shell call rather than via <see cref="DeleteAsync"/> (which prunes as it goes).
    /// Paths whose blob is already gone are fine; only empty shard dirs (below the store root) are removed.</summary>
    void PruneEmptyShards(IEnumerable<string> relativePaths);
}
