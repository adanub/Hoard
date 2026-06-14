using Hoard.Core.Storage;

namespace Hoard.Core.Projects;

/// <summary>
/// An <see cref="IMediaStore"/> that delegates to a <see cref="ContentAddressedStore"/> rooted in the
/// currently open project. It rebuilds the backing store when the project (and thus the store root)
/// changes, so callers can hold a single singleton instance across project switches.
/// </summary>
public sealed class ProjectMediaStore : IMediaStore
{
    private readonly ProjectManager _projects;
    private string? _cachedRoot;
    private ContentAddressedStore? _store;

    public ProjectMediaStore(ProjectManager projects) => _projects = projects;

    private ContentAddressedStore Store
    {
        get
        {
            var root = _projects.Current?.StoreRoot
                ?? throw new InvalidOperationException("No project is open. Create or open a project first.");
            if (_store is null || !string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _store = new ContentAddressedStore(root);
                _cachedRoot = root;
            }
            return _store;
        }
    }

    public Task<StoredBlob> PutAsync(string sourcePath, CancellationToken ct = default)
        => Store.PutAsync(sourcePath, ct);

    public string GetAbsolutePath(string relativePath) => Store.GetAbsolutePath(relativePath);

    public bool Exists(string sha256, string extension) => Store.Exists(sha256, extension);
}
