using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The human-readable export: a board's subtree materialises as a browsable Board/Folder/image tree,
/// live assets only, stable per-asset names, incremental on re-run, missing blobs reported not fatal.
/// </summary>
public class BoardExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-export-test", Guid.NewGuid().ToString("N"));
    private readonly string _dest;
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public BoardExporterTests()
    {
        Directory.CreateDirectory(_dir);
        _dest = Path.Combine(_dir, "export");
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BoardExporter Exporter => new(_dbFactory, _store);

    private Task<int> CreateBoardAsync(string name, int? parentId = null) =>
        new IngestService(_dbFactory, _store, Array.Empty<ISourceConnector>())
            .CreateBoardAsync(name, "", parentId: parentId);

    /// <summary>Stores real bytes in the CAS and links the resulting asset into a collection.</summary>
    private async Task<Asset> SeedAssetAsync(
        int collectionId, string content, string? title = null, string? pinId = null, bool deleted = false)
    {
        var temp = Path.Combine(_dir, "seed-" + Guid.NewGuid().ToString("N") + ".jpg");
        await File.WriteAllTextAsync(temp, content);
        var blob = await _store.PutAsync(temp);
        File.Delete(temp);

        await using var db = _dbFactory.CreateDbContext();
        var asset = new Asset
        {
            Sha256 = blob.Sha256,
            RelativePath = blob.RelativePath,
            Bytes = blob.Bytes,
            Kind = MediaKind.Image,
            SourceConnector = "pinterest",
            SourceId = pinId,
            Title = title,
            ImportedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
            DeletionNote = deleted ? "test tombstone" : null,
        };
        db.Assets.Add(asset);
        db.CollectionItems.Add(new CollectionItem
        {
            CollectionId = collectionId,
            Asset = asset,
            AddedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return asset;
    }

    [Fact]
    public async Task Export_materialises_the_subtree_with_readable_names_and_the_rename_override()
    {
        var boardId = await CreateBoardAsync("Interiors");
        var folderId = await CreateBoardAsync("Kitchen", parentId: boardId);
        await new CurationService(_dbFactory, _store).RenameBoardAsync(folderId, "Kitchen Ideas");
        await SeedAssetAsync(boardId, "loose-bytes", title: "Cosy nook", pinId: "111");
        await SeedAssetAsync(folderId, "folder-bytes", pinId: "222");

        var report = await Exporter.ExportAsync(boardId, _dest);

        Assert.Equal(new ExportReport(Copied: 2, UpToDate: 0, MissingBlobs: 0), report);
        var loose = Path.Combine(_dest, "Interiors", "Cosy nook [111].jpg");
        var foldered = Path.Combine(_dest, "Interiors", "Kitchen Ideas", "222.jpg");
        Assert.Equal("loose-bytes", await File.ReadAllTextAsync(loose));
        Assert.Equal("folder-bytes", await File.ReadAllTextAsync(foldered));
    }

    [Fact]
    public async Task Tombstoned_assets_never_export()
    {
        var boardId = await CreateBoardAsync("Interiors");
        await SeedAssetAsync(boardId, "live", pinId: "1");
        await SeedAssetAsync(boardId, "gone", pinId: "2", deleted: true);

        var report = await Exporter.ExportAsync(boardId, _dest);

        Assert.Equal(1, report.Copied);
        var files = Directory.GetFiles(Path.Combine(_dest, "Interiors"));
        Assert.Equal(new[] { "1.jpg" }, files.Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_missing_blob_is_counted_not_fatal()
    {
        var boardId = await CreateBoardAsync("Interiors");
        await SeedAssetAsync(boardId, "kept", pinId: "1");
        var lost = await SeedAssetAsync(boardId, "lost", pinId: "2");
        File.Delete(_store.GetAbsolutePath(lost.RelativePath));

        var report = await Exporter.ExportAsync(boardId, _dest);

        Assert.Equal(new ExportReport(Copied: 1, UpToDate: 0, MissingBlobs: 1), report);
        Assert.False(File.Exists(Path.Combine(_dest, "Interiors", "2.jpg")));
    }

    [Fact]
    public async Task Re_export_skips_up_to_date_files_and_repairs_a_torn_one()
    {
        var boardId = await CreateBoardAsync("Interiors");
        await SeedAssetAsync(boardId, "alpha-bytes", pinId: "1");
        await SeedAssetAsync(boardId, "beta-bytes!", pinId: "2");
        await Exporter.ExportAsync(boardId, _dest);

        var second = await Exporter.ExportAsync(boardId, _dest);
        Assert.Equal(new ExportReport(Copied: 0, UpToDate: 2, MissingBlobs: 0), second);

        // A torn/short destination file (crashed copy, external damage) is length-mismatched → re-copied.
        var torn = Path.Combine(_dest, "Interiors", "1.jpg");
        await File.WriteAllTextAsync(torn, "al");
        var third = await Exporter.ExportAsync(boardId, _dest);
        Assert.Equal(new ExportReport(Copied: 1, UpToDate: 1, MissingBlobs: 0), third);
        Assert.Equal("alpha-bytes", await File.ReadAllTextAsync(torn));
    }

    [Fact]
    public async Task Duplicate_titles_stay_unique_via_the_pin_id()
    {
        var boardId = await CreateBoardAsync("Interiors");
        await SeedAssetAsync(boardId, "first", title: "Lamp", pinId: "1");
        await SeedAssetAsync(boardId, "second", title: "Lamp", pinId: "2");

        await Exporter.ExportAsync(boardId, _dest);

        var files = Directory.GetFiles(Path.Combine(_dest, "Interiors")).Select(Path.GetFileName).OrderBy(n => n);
        Assert.Equal(new[] { "Lamp [1].jpg", "Lamp [2].jpg" }, files);
    }

    [Fact]
    public async Task Sibling_folders_that_sanitise_to_the_same_name_are_disambiguated()
    {
        var boardId = await CreateBoardAsync("Interiors");
        var a = await CreateBoardAsync("A/B", parentId: boardId);
        var b = await CreateBoardAsync("A:B", parentId: boardId);
        await SeedAssetAsync(a, "in-a", pinId: "1");
        await SeedAssetAsync(b, "in-b", pinId: "2");

        await Exporter.ExportAsync(boardId, _dest);

        var dirs = Directory.GetDirectories(Path.Combine(_dest, "Interiors"))
            .Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "A B", $"A B [{b}]" }, dirs);
    }
}
