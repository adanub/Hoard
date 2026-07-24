using Hoard.Core.Storage;
using Xunit;

namespace Hoard.Core.Tests;

public class ContentAddressedStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hoard-test-store", Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "hoard-test-work", Guid.NewGuid().ToString("N"));

    public ContentAddressedStoreTests() => Directory.CreateDirectory(_work);

    [Fact]
    public async Task Identical_content_dedupes_to_one_blob()
    {
        var store = new ContentAddressedStore(_root);
        var a = WriteTemp("a.jpg", "hello world");
        var b = WriteTemp("b.jpg", "hello world"); // same bytes, different name

        var first = await store.PutAsync(a);
        var second = await store.PutAsync(b);

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.RelativePath, second.RelativePath);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Different_content_produces_distinct_blobs()
    {
        var store = new ContentAddressedStore(_root);
        var first = await store.PutAsync(WriteTemp("a.jpg", "one"));
        var second = await store.PutAsync(WriteTemp("b.jpg", "two"));

        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal(2, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Shards_two_levels_and_preserves_extension()
    {
        var store = new ContentAddressedStore(_root);
        var result = await store.PutAsync(WriteTemp("a.png", "shard me"));

        // <sha[0:2]>/<sha[2:4]>/<sha>.png
        var parts = result.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(3, parts.Length);
        Assert.Equal(2, parts[0].Length);
        Assert.Equal(2, parts[1].Length);
        Assert.EndsWith(".png", parts[2]);
        Assert.StartsWith(parts[0] + parts[1], result.Sha256);
        Assert.True(File.Exists(store.GetAbsolutePath(result.RelativePath)));
    }

    [Fact]
    public async Task Relative_paths_are_stored_in_canonical_forward_slash_form()
    {
        // The relative path is persisted to the project DB, so it must be OS-independent — a project
        // written on Windows has to open on macOS/Linux and vice versa.
        var store = new ContentAddressedStore(_root);
        var result = await store.PutAsync(WriteTemp("a.jpg", "canonical"));

        Assert.DoesNotContain('\\', result.RelativePath);
        Assert.Equal(2, result.RelativePath.Count(c => c == '/'));
    }

    [Fact]
    public async Task Legacy_backslashed_relative_paths_resolve_and_delete()
    {
        // DBs written by Windows builds before the canonical form hold "ab\cd\<sha>.jpg" — the store
        // must resolve them on every OS (this was the "all tiles say file missing" cross-machine bug).
        var store = new ContentAddressedStore(_root);
        var result = await store.PutAsync(WriteTemp("a.jpg", "legacy"));
        var backslashed = result.RelativePath.Replace('/', '\\');

        Assert.True(File.Exists(store.GetAbsolutePath(backslashed)));

        await store.DeleteAsync(backslashed);
        Assert.False(File.Exists(store.GetAbsolutePath(result.RelativePath)));
        Assert.Empty(Directory.EnumerateDirectories(_root)); // shard dirs pruned via the same resolution
    }

    private string WriteTemp(string name, string content)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_work);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
