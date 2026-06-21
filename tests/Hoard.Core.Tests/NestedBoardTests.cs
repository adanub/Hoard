using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// Nested folders: a Pinterest section / sub-folder is a child <see cref="Collection"/> (via <c>ParentId</c>).
/// Top-level boards list apart from their child folders, a folder carries its own pins, moving re-files a pin
/// (and off the parent's grid), and deleting a board takes its whole subtree (recycling the blobs).
/// </summary>
public class NestedBoardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-nested-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public NestedBoardTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    [Fact]
    public async Task A_child_folder_is_excluded_from_top_level_boards_but_listed_under_its_parent()
    {
        var ingest = new IngestService(_dbFactory, _store, Array.Empty<ISourceConnector>());
        var boardId = await ingest.CreateBoardAsync("Interiors", "");
        var folderId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId, sectionId: "sec-1");

        var library = new LibraryService(_dbFactory, _store);
        var topLevel = await library.GetCollectionsAsync();
        Assert.Equal(new[] { "Interiors" }, topLevel.Select(c => c.Name)); // the folder is NOT a top-level board

        var children = await library.GetChildBoardsAsync(boardId);
        Assert.Equal(new[] { "Kitchen" }, children.Select(c => c.Name));

        await using var db = _dbFactory.CreateDbContext();
        var folder = await db.Collections.FirstAsync(c => c.Id == folderId);
        Assert.Equal(boardId, folder.ParentId);
        Assert.Equal("sec-1", folder.SourceSectionId);
    }

    [Fact]
    public async Task Moving_a_pin_into_a_folder_files_it_there_and_off_the_board_grid()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Interiors", "p1", "p2") });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);
        var folderId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId);

        int p1Id;
        await using (var db = _dbFactory.CreateDbContext())
            p1Id = await db.Assets.Where(a => a.SourceId == "p1").Select(a => a.Id).SingleAsync();

        var library = new LibraryService(_dbFactory, _store);
        Assert.Equal(2, (await library.GetAssetsAsync(boardId)).Count); // both pins loose on the board to start

        await new CurationService(_dbFactory, _store).MoveAssetWithinBoardAsync(p1Id, boardId, folderId);

        var board = await library.GetAssetsAsync(boardId);
        var folder = await library.GetAssetsAsync(folderId);
        Assert.Single(board);
        Assert.DoesNotContain(board, a => a.Id == p1Id);   // left the board grid
        Assert.Equal(new[] { p1Id }, folder.Select(a => a.Id)); // now in the folder

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(1, await db.CollectionItems.CountAsync(ci => ci.AssetId == p1Id)); // re-pointed, not a 2nd link
            var link = await db.CollectionItems.SingleAsync(ci => ci.AssetId == p1Id);
            Assert.Equal(folderId, link.CollectionId);
            Assert.Null(link.CollectionSourceId); // detached from per-source attribution once user-filed
        }
    }

    [Fact]
    public async Task Moving_a_pin_into_a_folder_it_already_occupies_drops_the_source_link()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Interiors", "p1") });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);
        var folderId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId);

        int p1Id;
        await using (var db = _dbFactory.CreateDbContext()) p1Id = await db.Assets.Select(a => a.Id).SingleAsync();

        // Link p1 into the folder too, so it sits in BOTH the board and the folder.
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CollectionItems.Add(new CollectionItem { CollectionId = folderId, AssetId = p1Id, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await new CurationService(_dbFactory, _store).MoveAssetWithinBoardAsync(p1Id, boardId, folderId);

        await using (var db = _dbFactory.CreateDbContext())
            // The board link is dropped (no unique-index violation); only the folder link remains.
            Assert.Equal(new[] { folderId },
                await db.CollectionItems.Where(ci => ci.AssetId == p1Id).Select(ci => ci.CollectionId).ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_board_deletes_its_nested_folders_and_their_images_too()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Interiors", "p1", "p2") });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        // Arbitrary depth: Interiors → Kitchen → Sink, with a pin filed into each level.
        var kitchenId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId);
        var sinkId = await ingest.CreateBoardAsync("Sink", "", parentId: kitchenId);

        int p1Id, p2Id;
        string p1Blob, p2Blob;
        await using (var db = _dbFactory.CreateDbContext())
        {
            p1Id = await db.Assets.Where(a => a.SourceId == "p1").Select(a => a.Id).SingleAsync();
            p2Id = await db.Assets.Where(a => a.SourceId == "p2").Select(a => a.Id).SingleAsync();
            p1Blob = _store.GetAbsolutePath(await db.Assets.Where(a => a.Id == p1Id).Select(a => a.RelativePath).SingleAsync());
            p2Blob = _store.GetAbsolutePath(await db.Assets.Where(a => a.Id == p2Id).Select(a => a.RelativePath).SingleAsync());
        }
        var curation = new CurationService(_dbFactory, _store);
        await curation.MoveAssetWithinBoardAsync(p1Id, boardId, kitchenId); // into Kitchen
        await curation.MoveAssetWithinBoardAsync(p2Id, boardId, sinkId);    // into Kitchen/Sink

        var recycler = new FakeRecycler();
        var removed = await new CurationService(_dbFactory, _store, recycler).DeleteBoardAsync(boardId);

        Assert.Equal(2, removed);                              // both images, across the whole subtree
        Assert.Equal(2, recycler.Recycled.Count);
        Assert.Contains(p1Blob, recycler.Recycled);
        Assert.Contains(p2Blob, recycler.Recycled);
        Assert.False(File.Exists(p1Blob));
        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(0, await db.Collections.CountAsync());     // board + Kitchen + Sink all gone
            Assert.Equal(0, await db.CollectionItems.CountAsync()); // links cascaded away
            Assert.Equal(0, await db.Assets.CountAsync());          // image rows GONE (not tombstoned)
            Assert.Equal(2, await db.SyncOps.CountAsync(o => o.Op == SyncOpKind.Remove));
        }
    }

    [Fact]
    public async Task Deleting_a_board_leaves_a_sibling_boards_folders_untouched()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "A", "a1") });
        var boardA = await ingest.CreateBoardAsync("A", "https://pinterest.com/jane/a/");
        await ingest.ImportAsync("https://pinterest.com/jane/a/", new ConnectorOptions(), null, boardA);
        await ingest.CreateBoardAsync("A-folder", "", parentId: boardA);

        var boardB = await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "B", "b1") })
            .CreateBoardAsync("B", "https://pinterest.com/jane/b/");
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "B", "b1") })
            .ImportAsync("https://pinterest.com/jane/b/", new ConnectorOptions(), null, boardB);
        var bFolder = await new IngestService(_dbFactory, _store, Array.Empty<ISourceConnector>())
            .CreateBoardAsync("B-folder", "", parentId: boardB);

        await new CurationService(_dbFactory, _store).DeleteBoardAsync(boardA);

        await using var db = _dbFactory.CreateDbContext();
        // Board B and its folder survive; only A's subtree is gone.
        Assert.Equal(new[] { "B", "B-folder" },
            await db.Collections.OrderBy(c => c.Name).Select(c => c.Name).ToListAsync());
        Assert.Equal(boardB, await db.Collections.Where(c => c.Id == bFolder).Select(c => c.ParentId).SingleAsync());
        Assert.Equal(1, await db.Assets.CountAsync(a => a.SourceId == "b1"));
    }

    // ── Auto-import of sections (Phase 3 ingest side) ──────────────────────────

    [Fact]
    public async Task Importing_sectioned_pins_files_them_into_child_folders_and_keeps_loose_pins_on_the_board()
    {
        var connector = new FakeSectionConnector("board-A", "Interiors",
            loose: new[] { "p1", "p2" },
            ("sec-1", "Kitchen", new[] { "k1", "k2" }),
            ("sec-2", "Bath", new[] { "b1" }));
        var ingest = new IngestService(_dbFactory, _store, new[] { connector });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        var library = new LibraryService(_dbFactory, _store);
        Assert.Equal(2, (await library.GetAssetsAsync(boardId)).Count);             // board grid = the 2 loose pins
        var folders = await library.GetChildBoardsAsync(boardId);
        Assert.Equal(new[] { "Bath", "Kitchen" }, folders.Select(f => f.Name));      // a folder per section (ordered)

        await using var db = _dbFactory.CreateDbContext();
        var kitchen = await db.Collections.FirstAsync(c => c.SourceSectionId == "sec-1");
        Assert.Equal(boardId, kitchen.ParentId);
        Assert.Equal(2, (await library.GetAssetsAsync(kitchen.Id)).Count);          // k1, k2 inside Kitchen
        Assert.Equal(5, await db.Assets.CountAsync());                              // p1,p2,k1,k2,b1
        Assert.Equal(3, await db.Collections.CountAsync());                        // board + 2 folders
    }

    [Fact]
    public async Task Reimporting_sectioned_pins_reuses_the_folder_and_does_not_duplicate()
    {
        var sections = new (string, string, string[])[] { ("sec-1", "Kitchen", new[] { "k1" }) };
        var boardId = await new IngestService(_dbFactory, _store,
                new[] { new FakeSectionConnector("board-A", "Interiors", Array.Empty<string>(), sections) })
            .CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        for (var i = 0; i < 2; i++)
            await new IngestService(_dbFactory, _store,
                    new[] { new FakeSectionConnector("board-A", "Interiors", Array.Empty<string>(), sections) })
                .ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.Collections.CountAsync(c => c.SourceSectionId == "sec-1")); // one folder, re-found
        Assert.Equal(2, await db.Collections.CountAsync());                                  // board + folder
        Assert.Equal(1, await db.CollectionItems.CountAsync());                              // k1 linked once
    }

    [Fact]
    public async Task Resync_pre_skips_already_held_section_pins()
    {
        var sections = new (string, string, string[])[] { ("sec-1", "Kitchen", new[] { "k1" }) };
        var ingest = new IngestService(_dbFactory, _store,
            new[] { new FakeSectionConnector("board-A", "Interiors", new[] { "p1" }, sections) });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        var spy = new FakeSectionConnector("board-A", "Interiors", new[] { "p1" }, sections);
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        Assert.NotNull(spy.LastOptions!.KnownItems);
        Assert.Contains(spy.LastOptions!.KnownItems!, k => k.BoardId == "board-A" && k.SourceId == "k1"); // section pin
        Assert.Contains(spy.LastOptions!.KnownItems!, k => k.BoardId == "board-A" && k.SourceId == "p1"); // loose pin
    }

    [Fact]
    public async Task A_boards_displayed_count_includes_images_in_its_sections()
    {
        var connector = new FakeSectionConnector("board-A", "Interiors",
            loose: new[] { "p1", "p2" },
            ("sec-1", "Kitchen", new[] { "k1", "k2", "k3" }));
        var ingest = new IngestService(_dbFactory, _store, new[] { connector });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        var library = new LibraryService(_dbFactory, _store);
        var board = (await library.GetCollectionsAsync()).Single();
        Assert.Equal(5, board.ItemCount);                              // 2 loose + 3 in the Kitchen section

        Assert.Equal(2, (await library.GetAssetsAsync(boardId)).Count); // the grid itself still shows loose pins only
        Assert.Equal(5, (await library.GetBoardDetailAsync(boardId))!.Images); // Edit popup counts span the subtree too
    }

    [Fact]
    public async Task A_folder_card_count_rolls_up_its_own_subfolders()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Interiors", "p1", "p2", "p3") });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);
        var kitchen = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId);
        var sink = await ingest.CreateBoardAsync("Sink", "", parentId: kitchen);

        int p1, p2;
        await using (var db = _dbFactory.CreateDbContext())
        {
            p1 = await db.Assets.Where(a => a.SourceId == "p1").Select(a => a.Id).SingleAsync();
            p2 = await db.Assets.Where(a => a.SourceId == "p2").Select(a => a.Id).SingleAsync();
        }
        var curation = new CurationService(_dbFactory, _store);
        await curation.MoveAssetWithinBoardAsync(p1, boardId, kitchen); // into Kitchen
        await curation.MoveAssetWithinBoardAsync(p2, boardId, sink);    // into Kitchen/Sink

        var library = new LibraryService(_dbFactory, _store);
        Assert.Equal(3, (await library.GetCollectionsAsync()).Single().ItemCount); // whole board: p3 loose + p1 + p2
        var kitchenCard = (await library.GetChildBoardsAsync(boardId)).Single(c => c.Name == "Kitchen");
        Assert.Equal(2, kitchenCard.ItemCount);                                    // Kitchen's own p1 + Sink's p2
    }

    [Fact]
    public async Task Cover_assets_are_spread_across_the_board_most_recent_midpoint_oldest()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "B", "p1", "p2", "p3", "p4", "p5") });
        var boardId = await ingest.CreateBoardAsync("B", "https://pinterest.com/jane/b/");
        await ingest.ImportAsync("https://pinterest.com/jane/b/", new ConnectorOptions(), null, boardId);

        List<string> byRecency;
        await using (var db = _dbFactory.CreateDbContext())
            byRecency = await db.Assets.OrderByDescending(a => a.Id).Select(a => a.Sha256).ToListAsync();
        // 5 assets → spread positions 0, 2, 4 of the recency order = newest, midpoint, oldest (not the 3 newest).
        var expected = new[] { byRecency[0], byRecency[2], byRecency[4] };

        var covers = await new LibraryService(_dbFactory, _store).GetCoverAssetsAsync(boardId, 3);
        Assert.Equal(expected, covers.Select(c => c.Sha256));
    }

    [Fact]
    public async Task A_board_whose_pins_are_all_sectioned_still_gets_a_cover()
    {
        var connector = new FakeSectionConnector("board-A", "Interiors", Array.Empty<string>(),
            ("sec-1", "Kitchen", new[] { "k1", "k2" }));
        var ingest = new IngestService(_dbFactory, _store, new[] { connector });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        // The board has no loose pins, so its cover must come from the Kitchen section (subtree-aware covers).
        Assert.NotEmpty(await new LibraryService(_dbFactory, _store).GetCoverAssetsAsync(boardId, 3));
    }

    [Fact]
    public async Task Removing_a_source_also_removes_its_pins_filed_into_section_folders()
    {
        var sections = new (string, string, string[])[] { ("sec-1", "Kitchen", new[] { "k1" }) };
        var ingest = new IngestService(_dbFactory, _store,
            new[] { new FakeSectionConnector("board-A", "Interiors", new[] { "p1" }, sections) });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        int sourceId;
        await using (var db = _dbFactory.CreateDbContext())
            sourceId = await db.CollectionSources.Select(s => s.Id).SingleAsync();

        var removed = await new CurationService(_dbFactory, _store).RemoveSourceAsync(sourceId);

        Assert.Equal(2, removed); // p1 (loose) AND k1 (filed in the Kitchen folder) both go with the source
        await using (var db = _dbFactory.CreateDbContext())
            Assert.Equal(0, await db.Assets.CountAsync()); // nothing left orphaned in a folder
    }

    [Fact]
    public async Task Moving_a_pin_it_already_occupies_detaches_the_surviving_links_source_attribution()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Interiors", "p1") });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);
        var folderId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId);

        int p1Id, sourceId;
        await using (var db = _dbFactory.CreateDbContext())
        {
            p1Id = await db.Assets.Select(a => a.Id).SingleAsync();
            sourceId = await db.CollectionSources.Select(s => s.Id).SingleAsync();
            // p1 is also in the folder, attributed to the source (as a sectioned import would leave it).
            db.CollectionItems.Add(new CollectionItem
            { CollectionId = folderId, AssetId = p1Id, CollectionSourceId = sourceId, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await new CurationService(_dbFactory, _store).MoveAssetWithinBoardAsync(p1Id, boardId, folderId);

        await using (var db = _dbFactory.CreateDbContext())
        {
            var link = await db.CollectionItems.SingleAsync(ci => ci.AssetId == p1Id);
            Assert.Equal(folderId, link.CollectionId);
            Assert.Null(link.CollectionSourceId); // detached, so a later remove-source won't sweep the user's pin
        }
    }

    [Fact]
    public async Task Board_asset_shas_span_the_subtree_so_an_all_sectioned_board_is_not_seen_as_empty()
    {
        var connector = new FakeSectionConnector("board-A", "Interiors", Array.Empty<string>(),
            ("sec-1", "Kitchen", new[] { "k1", "k2" }));
        var ingest = new IngestService(_dbFactory, _store, new[] { connector });
        var boardId = await ingest.CreateBoardAsync("Interiors", "https://pinterest.com/jane/interiors/");
        await ingest.ImportAsync("https://pinterest.com/jane/interiors/", new ConnectorOptions(), null, boardId);

        // No loose pins on the board, but the Kitchen section has 2 — the shas (the failed-import empty test) must
        // include them, so the board is not wrongly judged empty + discarded.
        var shas = await new LibraryService(_dbFactory, _store).GetBoardAssetShasAsync(boardId);
        Assert.Equal(2, shas.Count);
    }

    /// <summary>A connector serving one board with both loose (sectionless) pins and pins grouped into sections.</summary>
    private sealed class FakeSectionConnector : ISourceConnector
    {
        private readonly string _boardId;
        private readonly string _boardName;
        private readonly string[] _loose;
        private readonly (string SectionId, string SectionName, string[] Pins)[] _sections;

        public FakeSectionConnector(string boardId, string boardName, string[] loose,
            params (string SectionId, string SectionName, string[] Pins)[] sections)
        {
            _boardId = boardId;
            _boardName = boardName;
            _loose = loose;
            _sections = sections;
        }

        public ConnectorOptions? LastOptions { get; private set; }
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            LastOptions = options;
            var temp = Path.Combine(Path.GetTempPath(), "hoard-section-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var pin in _loose)
                    await Emit(temp, pin, null, null, url, onItem, ct);
                foreach (var (sectionId, sectionName, pins) in _sections)
                    foreach (var pin in pins)
                        await Emit(temp, pin, sectionId, sectionName, url, onItem, ct);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        private async Task Emit(string temp, string pin, string? sectionId, string? sectionName, string url,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var file = Path.Combine(temp, $"{pin}.jpg");
            await File.WriteAllTextAsync(file, pin, ct);
            await onItem(new SourceMediaItem
            {
                FilePath = file,
                Connector = Name,
                SourceId = pin,
                BoardId = _boardId,
                BoardName = _boardName,
                BoardUrl = url,
                SectionId = sectionId,
                SectionName = sectionName,
                Title = pin,
                RawJson = $"{{\"board\":{{\"id\":\"{_boardId}\"}}}}",
            }, ct);
        }
    }

    /// <summary>Records recycled paths and removes the file (so File.Exists is false), standing in for the OS bin.</summary>
    private sealed class FakeRecycler : IFileRecycler
    {
        public List<string> Recycled { get; } = new();
        public void RecycleDirectory(string path) => Recycle(path);
        public void RecycleFile(string path) => Recycle(path);
        public void RecycleFiles(IReadOnlyCollection<string> paths) { foreach (var p in paths) Recycle(p); }
        private void Recycle(string path)
        {
            Recycled.Add(path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static FakeBoardConnector Connector(string boardId, string boardName, params string[] pins) =>
        new(boardId, boardName, pins);

    /// <summary>A connector serving one named source board with the given pins (content == pin id).</summary>
    private sealed class FakeBoardConnector : ISourceConnector
    {
        private readonly string _boardId;
        private readonly string _boardName;
        private readonly string[] _pins;

        public FakeBoardConnector(string boardId, string boardName, string[] pins)
        {
            _boardId = boardId;
            _boardName = boardName;
            _pins = pins;
        }

        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hoard-nested-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var pin in _pins)
                {
                    var file = Path.Combine(temp, $"{pin}.jpg");
                    await File.WriteAllTextAsync(file, pin, ct);
                    await onItem(new SourceMediaItem
                    {
                        FilePath = file,
                        Connector = Name,
                        SourceId = pin,
                        BoardId = _boardId,
                        BoardName = _boardName,
                        BoardUrl = url,
                        Title = pin,
                        RawJson = $"{{\"board\":{{\"id\":\"{_boardId}\"}}}}",
                    }, ct);
                }
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
