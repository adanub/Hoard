using Hoard.Core.Domain;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// An incremental sync stops each target once it reaches images it already holds — which means every part of
/// a board that has to be checked must BE a target. gallery-dl appends a board's sections after all of its
/// pins, i.e. beyond where the stop lands, so the sections have to be enumerated here or a delta sync would
/// quietly stop syncing every folder.
/// </summary>
public class SyncTargetTests : IDisposable
{
    private const string BoardUrl = "https://www.pinterest.com/jane/reference-photos/";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-targets-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public SyncTargetTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    private LibraryService Library => new(_dbFactory, _store);

    /// <summary>A board with one source, plus a pin per (sourceBoardId, sectionId) pair given.</summary>
    private async Task<int> SeedBoardAsync(
        string sourceBoardId, string sourceUrl, params (string Board, string? Section, bool Deleted)[] pins)
    {
        await using var db = _dbFactory.CreateDbContext();
        var board = new Collection { Name = "Reference photos", SourceConnector = "pinterest", CreatedAt = DateTimeOffset.UtcNow };
        db.Collections.Add(board);
        db.CollectionSources.Add(new CollectionSource
        {
            Collection = board,
            SourceConnector = "pinterest",
            SourceBoardId = sourceBoardId,
            SourceUrl = sourceUrl,
            AddedAt = DateTimeOffset.UtcNow,
        });

        foreach (var (pinBoard, section, deleted) in pins)
        {
            var sha = Guid.NewGuid().ToString("N");
            var asset = new Asset
            {
                Sha256 = sha,
                RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
                Kind = MediaKind.Image,
                SourceConnector = "pinterest",
                SourceId = sha,
                SourceBoardId = pinBoard,
                SourceSectionId = section,
                DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
                ImportedAt = DateTimeOffset.UtcNow,
            };
            db.Assets.Add(asset);
            db.CollectionItems.Add(new CollectionItem { Collection = board, Asset = asset, AddedAt = DateTimeOffset.UtcNow });
        }

        await db.SaveChangesAsync();
        return board.Id;
    }

    [Fact]
    public async Task Each_section_the_board_holds_pins_from_becomes_its_own_target()
    {
        var id = await SeedBoardAsync("b1", BoardUrl,
            ("b1", null, false),   // a loose pin — the board target covers it
            ("b1", "s1", false),
            ("b1", "s2", false),
            ("b1", "s1", false));  // same section again: still one target

        var targets = await Library.GetSyncTargetsAsync(id);

        Assert.Equal(
        [
            BoardUrl,
            "https://www.pinterest.com/jane/reference-photos/id:s1",
            "https://www.pinterest.com/jane/reference-photos/id:s2",
        ], targets);
    }

    [Fact]
    public async Task A_board_with_no_sections_is_just_its_source_url()
    {
        var id = await SeedBoardAsync("b1", BoardUrl, ("b1", null, false));

        Assert.Equal([BoardUrl], await Library.GetSyncTargetsAsync(id));
    }

    [Fact]
    public async Task A_section_known_only_from_a_deleted_pin_and_no_folder_is_not_a_target()
    {
        // A tombstoned pin is blacklisted, never re-fetched, and with no folder left there's nothing here
        // that can gain new items — so crawling it would be a page request per sync spent on nothing.
        var id = await SeedBoardAsync("b1", BoardUrl, ("b1", "gone", true));

        Assert.Equal([BoardUrl], await Library.GetSyncTargetsAsync(id));
    }

    [Fact]
    public async Task An_empty_folder_is_still_a_target_when_its_source_board_is_unambiguous()
    {
        // A folder whose pins predate provenance (or were all deleted) can still gain new ones, and with a
        // single source there's no doubt which board to ask.
        var id = await SeedBoardAsync("b1", BoardUrl);
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Collections.Add(new Collection
            {
                Name = "Faces", SourceConnector = "pinterest", ParentId = id,
                SourceSectionId = "s9", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            [BoardUrl, "https://www.pinterest.com/jane/reference-photos/id:s9"],
            await Library.GetSyncTargetsAsync(id));
    }

    [Fact]
    public async Task A_section_is_asked_of_the_source_board_it_actually_came_from()
    {
        // A merged board gathers from two sources; each section belongs to exactly one of them, so asking
        // the wrong board for it would crawl a URL that doesn't exist.
        var id = await SeedBoardAsync("b1", BoardUrl, ("b1", "s1", false));
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CollectionSources.Add(new CollectionSource
            {
                CollectionId = id,
                SourceConnector = "pinterest",
                SourceBoardId = "b2",
                SourceUrl = "https://www.pinterest.com/jane/second-board/",
                AddedAt = DateTimeOffset.UtcNow,
            });
            var sha = Guid.NewGuid().ToString("N");
            var asset = new Asset
            {
                Sha256 = sha,
                RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
                Kind = MediaKind.Image,
                SourceConnector = "pinterest",
                SourceId = sha,
                SourceBoardId = "b2",
                SourceSectionId = "s2",
                ImportedAt = DateTimeOffset.UtcNow,
            };
            db.Assets.Add(asset);
            db.CollectionItems.Add(new CollectionItem { CollectionId = id, Asset = asset, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var targets = await Library.GetSyncTargetsAsync(id);

        Assert.Equal(
        [
            BoardUrl,
            "https://www.pinterest.com/jane/reference-photos/id:s1",
            "https://www.pinterest.com/jane/second-board/",
            "https://www.pinterest.com/jane/second-board/id:s2",
        ], targets);
    }

    [Fact]
    public async Task Sections_of_a_child_folder_count_too()
    {
        // The board's pins live in child folders, so the subtree — not just the board row — is what says
        // which sections exist.
        var id = await SeedBoardAsync("b1", BoardUrl);
        await using (var db = _dbFactory.CreateDbContext())
        {
            var folder = new Collection
            {
                Name = "Faces", SourceConnector = "pinterest", ParentId = id,
                SourceSectionId = "s1", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Collections.Add(folder);
            var sha = Guid.NewGuid().ToString("N");
            var asset = new Asset
            {
                Sha256 = sha,
                RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
                Kind = MediaKind.Image,
                SourceConnector = "pinterest",
                SourceId = sha,
                SourceBoardId = "b1",
                SourceSectionId = "s1",
                ImportedAt = DateTimeOffset.UtcNow,
            };
            db.Assets.Add(asset);
            db.CollectionItems.Add(new CollectionItem { Collection = folder, Asset = asset, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            [BoardUrl, "https://www.pinterest.com/jane/reference-photos/id:s1"],
            await Library.GetSyncTargetsAsync(id));
    }

    [Fact]
    public async Task A_local_board_with_no_sources_has_nothing_to_sync()
    {
        await using var db = _dbFactory.CreateDbContext();
        var board = new Collection { Name = "Local", SourceConnector = "", CreatedAt = DateTimeOffset.UtcNow };
        db.Collections.Add(board);
        await db.SaveChangesAsync();

        Assert.Empty(await Library.GetSyncTargetsAsync(board.Id));
    }

    // ── Syncing the whole project ──────────────────────────────────────────────

    [Fact]
    public async Task The_project_plan_covers_every_syncable_board_with_its_own_targets()
    {
        var first = await SeedBoardAsync("b1", BoardUrl, ("b1", "s1", false));
        var second = await SeedBoardAsync("b2", "https://www.pinterest.com/jane/second-board/", ("b2", null, false));

        var plans = await Library.GetProjectSyncPlanAsync();

        Assert.Equal([first, second], plans.Select(p => p.CollectionId));
        Assert.Equal([BoardUrl, "https://www.pinterest.com/jane/reference-photos/id:s1"], plans[0].Targets);
        Assert.Equal(["https://www.pinterest.com/jane/second-board/"], plans[1].Targets);
    }

    [Fact]
    public async Task The_project_plan_skips_boards_with_nothing_to_crawl()
    {
        // A purely local board has no source to re-fetch from: syncing it would be a no-op run, and listing it
        // would make the run report a board count it never touched.
        var real = await SeedBoardAsync("b1", BoardUrl, ("b1", null, false));
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Collections.Add(new Collection { Name = "Local", SourceConnector = "", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var plans = await Library.GetProjectSyncPlanAsync();

        Assert.Equal([real], plans.Select(p => p.CollectionId));
    }

    [Fact]
    public async Task The_project_plan_lists_top_level_boards_only_and_prefers_a_local_rename()
    {
        // Folders are crawled as targets OF their board, never as boards in their own right — listing one
        // would sync its parent's subtree twice.
        var id = await SeedBoardAsync("b1", BoardUrl, ("b1", "s1", false));
        await using (var db = _dbFactory.CreateDbContext())
        {
            var board = await db.Collections.FindAsync(id);
            board!.DisplayName = "My renamed board";
            db.Collections.Add(new Collection
            {
                Name = "Faces", SourceConnector = "pinterest", ParentId = id,
                SourceSectionId = "s1", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var plans = await Library.GetProjectSyncPlanAsync();

        Assert.Equal([id], plans.Select(p => p.CollectionId));
        Assert.Equal("My renamed board", plans[0].Name);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
