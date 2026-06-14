using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

public class CurationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-curation-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public CurationServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    /// <summary>Put a file into the store and wire up an Asset with a board link and a tag.</summary>
    private async Task<(int assetId, string relativePath)> SeedAssetAsync(string content)
    {
        var src = Path.Combine(_dir, $"src-{Guid.NewGuid():N}.jpg");
        await File.WriteAllTextAsync(src, content);
        var blob = await _store.PutAsync(src);

        await using var db = _dbFactory.CreateDbContext();
        var asset = new Asset
        {
            Sha256 = blob.Sha256,
            RelativePath = blob.RelativePath,
            Bytes = blob.Bytes,
            Kind = MediaKind.Image,
            SourceConnector = "pinterest",
            ImportedAt = DateTimeOffset.UtcNow,
        };
        var collection = new Collection { Name = "Nature", SourceConnector = "pinterest" };
        var tag = new Tag { Name = $"tag-{content}" }; // unique per seed (Tag.Name has a unique index)
        asset.CollectionItems.Add(new CollectionItem { Collection = collection, Asset = asset });
        asset.AssetTags.Add(new AssetTag { Asset = asset, Tag = tag });
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return (asset.Id, blob.RelativePath);
    }

    [Fact]
    public async Task Delete_tombstones_the_row_keeps_links_frees_the_blob_and_logs_a_Remove_op()
    {
        var (assetId, relativePath) = await SeedAssetAsync("hello-curate");
        var blobPath = _store.GetAbsolutePath(relativePath);
        Assert.True(File.Exists(blobPath));

        var curation = new CurationService(_dbFactory, _store);
        var sha = await curation.DeleteAssetAsync(assetId, "no longer wanted");

        Assert.NotNull(sha);
        Assert.False(File.Exists(blobPath));                       // blob freed from disk

        await using var db = _dbFactory.CreateDbContext();
        var asset = Assert.Single(await db.Assets.ToListAsync());  // row KEPT as a tombstone
        Assert.NotNull(asset.DeletedAt);
        Assert.Equal("no longer wanted", asset.DeletionNote);
        Assert.Equal(1, await db.CollectionItems.CountAsync());    // board link kept (tile stays in place)
        Assert.Equal(1, await db.AssetTags.CountAsync());          // tag link kept

        var op = Assert.Single(await db.SyncOps.ToListAsync());
        Assert.Equal(SyncOpKind.Remove, op.Op);
        Assert.Equal(sha, op.EntityKey);
    }

    [Fact]
    public async Task Delete_requires_a_note()
    {
        var (assetId, _) = await SeedAssetAsync("needs-note");
        var curation = new CurationService(_dbFactory, _store);
        await Assert.ThrowsAsync<ArgumentException>(() => curation.DeleteAssetAsync(assetId, "  "));
    }

    [Fact]
    public async Task Delete_of_missing_or_already_deleted_returns_null()
    {
        var curation = new CurationService(_dbFactory, _store);
        Assert.Null(await curation.DeleteAssetAsync(999_999, "gone"));

        var (assetId, _) = await SeedAssetAsync("twice");
        Assert.NotNull(await curation.DeleteAssetAsync(assetId, "first"));
        Assert.Null(await curation.DeleteAssetAsync(assetId, "second")); // already a tombstone

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.SyncOps.CountAsync()); // only the first delete logged a Remove
    }

    [Fact]
    public async Task Deleting_one_of_two_assets_leaves_the_other_live()
    {
        var (keepId, keepPath) = await SeedAssetAsync("keep-me");
        var (dropId, dropPath) = await SeedAssetAsync("drop-me");

        await new CurationService(_dbFactory, _store).DeleteAssetAsync(dropId, "spam");

        Assert.True(File.Exists(_store.GetAbsolutePath(keepPath)));
        Assert.False(File.Exists(_store.GetAbsolutePath(dropPath)));

        await using var db = _dbFactory.CreateDbContext();
        var keep = await db.Assets.FirstAsync(a => a.Id == keepId);
        var drop = await db.Assets.FirstAsync(a => a.Id == dropId);
        Assert.Null(keep.DeletedAt);     // still live
        Assert.NotNull(drop.DeletedAt);  // tombstoned
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
