using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

public class IngestServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-ingest-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public IngestServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    [Fact]
    public async Task First_import_creates_assets_board_and_links()
    {
        var connector = new FakeConnector(("AAA", "Nature"), ("BBB", "Nature"));
        var ingest = new IngestService(_dbFactory, _store, new[] { connector });

        var result = await ingest.ImportAsync("https://pinterest.com/jane/nature/", new ConnectorOptions(), null);

        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.NewAssets);
        Assert.Equal(0, result.DuplicateAssets);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(2, await db.Assets.CountAsync());
        Assert.Equal(1, await db.Collections.CountAsync());
        Assert.Equal(2, await db.CollectionItems.CountAsync());
    }

    [Fact]
    public async Task Reimport_is_idempotent_no_duplicate_assets_or_links()
    {
        var ingest = new IngestService(_dbFactory, _store,
            new[] { new FakeConnector(("AAA", "Nature"), ("BBB", "Nature")) });
        await ingest.ImportAsync("https://pinterest.com/jane/nature/", new ConnectorOptions(), null);

        // Fresh connector instance returns the same content again (simulating a re-run).
        var second = new IngestService(_dbFactory, _store,
            new[] { new FakeConnector(("AAA", "Nature"), ("BBB", "Nature")) });
        var result = await second.ImportAsync("https://pinterest.com/jane/nature/", new ConnectorOptions(), null);

        Assert.Equal(0, result.NewAssets);
        Assert.Equal(2, result.DuplicateAssets);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(2, await db.Assets.CountAsync());
        Assert.Equal(2, await db.CollectionItems.CountAsync());
    }

    [Fact]
    public async Task Same_image_in_two_boards_is_stored_once_linked_twice()
    {
        var ingest = new IngestService(_dbFactory, _store,
            new[] { new FakeConnector(("SAME", "BoardA"), ("SAME", "BoardB")) });

        var result = await ingest.ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        Assert.Equal(1, result.NewAssets);     // one unique blob
        Assert.Equal(1, result.DuplicateAssets);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.Assets.CountAsync());
        Assert.Equal(2, await db.Collections.CountAsync());
        Assert.Equal(2, await db.CollectionItems.CountAsync());
    }

    [Fact]
    public async Task LibraryService_filters_assets_by_collection()
    {
        await new IngestService(_dbFactory, _store,
                new[] { new FakeConnector(("AAA", "Nature"), ("BBB", "City")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        var library = new LibraryService(_dbFactory, _store);
        var collections = await library.GetCollectionsAsync();
        var nature = collections.First(c => c.Name == "Nature");

        Assert.Equal(2, await library.GetAssetCountAsync());
        Assert.Single(await library.GetAssetsAsync(nature.Id));
        Assert.Equal(2, (await library.GetAssetsAsync(null)).Count);
        Assert.All(await library.GetAssetsAsync(null), v => Assert.NotEmpty(v.Sha256)); // hash for thumbnail cache
    }

    [Fact]
    public async Task Search_filters_by_title_case_insensitively()
    {
        // Three pins → titles "Item 0", "Item 1", "Item 2".
        await new IngestService(_dbFactory, _store,
                new[] { new FakeConnector(("AAA", "B"), ("BBB", "B"), ("CCC", "B")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        var library = new LibraryService(_dbFactory, _store);

        Assert.Single(await library.GetAssetsAsync(null, "Item 1"));   // exact-ish substring
        Assert.Single(await library.GetAssetsAsync(null, "item 2"));   // case-insensitive
        Assert.Equal(3, (await library.GetAssetsAsync(null, "ITEM")).Count);
        Assert.Empty(await library.GetAssetsAsync(null, "nonexistent"));
        Assert.Equal(3, (await library.GetAssetsAsync(null, "   ")).Count); // blank = no filter
    }

    [Fact]
    public async Task Search_is_scoped_to_the_selected_collection()
    {
        await new IngestService(_dbFactory, _store,
                new[] { new FakeConnector(("AAA", "Nature"), ("BBB", "City")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        var library = new LibraryService(_dbFactory, _store);
        var nature = (await library.GetCollectionsAsync()).First(c => c.Name == "Nature");

        Assert.Equal(2, (await library.GetAssetsAsync(null, "Item")).Count);  // both pins match across all
        Assert.Single(await library.GetAssetsAsync(nature.Id, "Item 0"));     // Item 0 is the Nature pin
        Assert.Empty(await library.GetAssetsAsync(nature.Id, "Item 1"));      // Item 1 is in City, not Nature
    }

    [Fact]
    public async Task GetAssetDetail_returns_full_metadata_and_boards()
    {
        await new IngestService(_dbFactory, _store,
                new[] { new FakeConnector(("AAA", "Nature")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        var library = new LibraryService(_dbFactory, _store);
        var asset = (await library.GetAssetsAsync(null)).Single();

        var detail = await library.GetAssetDetailAsync(asset.Id);

        Assert.NotNull(detail);
        Assert.Equal(asset.Id, detail!.Id);
        Assert.Equal("Item 0", detail.Title);
        Assert.Equal(3, detail.Bytes); // "AAA"
        Assert.Equal(new[] { "Nature" }, detail.Boards);
        Assert.True(detail.ImportedAt > DateTimeOffset.MinValue);
        Assert.Null(await library.GetAssetDetailAsync(999_999)); // missing id
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Stand-in connector that writes the given (content, board) pairs to disk as .jpg files.</summary>
    private sealed class FakeConnector : ISourceConnector
    {
        private readonly (string Content, string Board)[] _items;
        public FakeConnector(params (string, string)[] items) => _items = items;

        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hoard-fake-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                for (var i = 0; i < _items.Length; i++)
                {
                    var (content, board) = _items[i];
                    var file = Path.Combine(temp, $"{i}_{content}.jpg");
                    File.WriteAllText(file, content);
                    await onItem(new SourceMediaItem
                    {
                        FilePath = file,
                        Connector = Name,
                        SourceId = $"pin{i}",
                        BoardName = board,
                        BoardId = board, // distinct boards get distinct ids in this fake
                        Title = $"Item {i}",
                    }, ct);
                }
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }
    }
}
