using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The board-merge model: one local board can gather pins from several Pinterest source boards, carries a
/// non-destructive rename override, and records each source for re-sync/removal.
/// </summary>
public class BoardMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-merge-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public BoardMergeTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    [Fact]
    public async Task Importing_two_boards_into_one_target_records_both_sources()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        // Merge a second Pinterest board into the SAME local board.
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "p1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.Collections.CountAsync());                 // still one local board
        Assert.Equal(3, await db.CollectionItems.CountAsync());             // all 3 pins linked into it
        var sources = await db.CollectionSources.OrderBy(s => s.SourceBoardId).ToListAsync();
        Assert.Equal(new[] { "board-A", "board-B" }, sources.Select(s => s.SourceBoardId));
        Assert.Equal(new[] { "Animals", "Plants" }, sources.Select(s => s.Name));
    }

    [Fact]
    public async Task Reimporting_the_same_source_does_not_duplicate_its_source_row()
    {
        var boardId = await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") })
            .CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        for (var i = 0; i < 2; i++)
            await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") })
                .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Single(await db.CollectionSources.ToListAsync());
    }

    [Fact]
    public async Task GetBoardDetail_returns_counts_and_every_merged_source()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "p1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        // Exercises the Edit-popup read path end to end — guards the SQLite "can't ORDER BY DateTimeOffset" trap.
        var detail = await new LibraryService(_dbFactory, _store).GetBoardDetailAsync(boardId);

        Assert.NotNull(detail);
        Assert.Equal(3, detail!.Images);
        Assert.Equal(new[] { "Animals", "Plants" }, detail.Sources.Select(s => s.Name));
    }

    [Fact]
    public async Task Rename_sets_the_display_override_and_keeps_the_source_name()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("Animals", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        await new CurationService(_dbFactory, _store).RenameBoardAsync(boardId, "Cute things");

        await using var db = _dbFactory.CreateDbContext();
        var board = await db.Collections.FirstAsync(c => c.Id == boardId);
        Assert.Equal("Cute things", board.DisplayName);
        Assert.Equal("Animals", board.Name); // original name preserved

        // The library projects the override as the shown name.
        var view = (await new LibraryService(_dbFactory, _store).GetCollectionsAsync()).Single();
        Assert.Equal("Cute things", view.Name);
    }

    [Fact]
    public async Task Each_imported_pin_is_attributed_to_its_source()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        await using var db = _dbFactory.CreateDbContext();
        var sourceId = await db.CollectionSources.Select(s => s.Id).SingleAsync();
        var items = await db.CollectionItems.ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, ci => Assert.Equal(sourceId, ci.CollectionSourceId)); // each link points at its source
    }

    [Fact]
    public async Task Removing_a_source_in_a_multi_source_board_deletes_only_that_sources_pins()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "b1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        int sourceA;
        string a1Blob, b1Blob;
        await using (var db = _dbFactory.CreateDbContext())
        {
            sourceA = await db.CollectionSources.Where(s => s.SourceBoardId == "board-A").Select(s => s.Id).SingleAsync();
            a1Blob = _store.GetAbsolutePath(await db.Assets.Where(a => a.SourceId == "a1").Select(a => a.RelativePath).SingleAsync());
            b1Blob = _store.GetAbsolutePath(await db.Assets.Where(a => a.SourceId == "b1").Select(a => a.RelativePath).SingleAsync());
        }

        var recycler = new FakeRecycler();
        var removed = await new CurationService(_dbFactory, _store, recycler).RemoveSourceAsync(sourceA);

        Assert.Equal(2, removed);                   // a1 + a2 (board-B's pin stays — B is still a source)
        Assert.Contains(a1Blob, recycler.Recycled); // their files went to the recycle bin
        Assert.False(File.Exists(a1Blob));          // and are no longer in the store
        Assert.True(File.Exists(b1Blob));           // board-B untouched

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Empty(await db.CollectionSources.Where(s => s.SourceBoardId == "board-A").ToListAsync());
            Assert.Equal(0, await db.Assets.CountAsync(a => a.SourceId == "a1" || a.SourceId == "a2")); // rows GONE
            Assert.Equal(1, await db.Assets.CountAsync());                                              // only b1 left
            Assert.Null(await db.Assets.Where(a => a.SourceId == "b1").Select(a => a.DeletedAt).SingleAsync()); // b1 live
            Assert.Equal(2, await db.ArchiveOps.CountAsync(o => o.Kind == Hoard.Core.Sync.ArchiveOpKinds.AssetRemoved)); // one removed op per deleted asset
        }
    }

    [Fact]
    public async Task Removing_the_only_source_deletes_all_the_boards_images()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        int sourceId; string blob;
        await using (var db = _dbFactory.CreateDbContext())
        {
            sourceId = await db.CollectionSources.Select(s => s.Id).SingleAsync();
            blob = _store.GetAbsolutePath(await db.Assets.Select(a => a.RelativePath).FirstAsync());
        }

        // No recycler → permanent delete (the headless/test fallback).
        var removed = await new CurationService(_dbFactory, _store).RemoveSourceAsync(sourceId);

        Assert.Equal(2, removed);                                          // the board is emptied of its pins
        Assert.False(File.Exists(blob));                                   // blob permanently freed
        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Empty(await db.CollectionSources.ToListAsync());        // source record gone
            Assert.Equal(0, await db.Assets.CountAsync());                 // both rows GONE (not tombstoned)
            var board = await db.Collections.FirstAsync(c => c.Id == boardId);
            Assert.Null(board.SourceBoardId);                             // primary pointer cleared
        }
    }

    [Fact]
    public async Task Removing_the_last_source_sweeps_unattributed_pins_too()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "b1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        int sourceA, sourceB;
        await using (var db = _dbFactory.CreateDbContext())
        {
            sourceA = await db.CollectionSources.Where(s => s.SourceBoardId == "board-A").Select(s => s.Id).SingleAsync();
            sourceB = await db.CollectionSources.Where(s => s.SourceBoardId == "board-B").Select(s => s.Id).SingleAsync();
            // Orphan b1's link (no source) to mimic a pin imported before per-pin provenance.
            var orphan = await db.CollectionItems.FirstAsync(ci => ci.CollectionSourceId == sourceB);
            orphan.CollectionSourceId = null;
            await db.SaveChangesAsync();
        }

        var curation = new CurationService(_dbFactory, _store);
        Assert.Equal(1, await curation.RemoveSourceAsync(sourceA)); // B remains → only a1
        Assert.Equal(1, await curation.RemoveSourceAsync(sourceB)); // last source → sweeps the orphaned b1

        await using (var db = _dbFactory.CreateDbContext())
            Assert.Equal(0, await db.Assets.CountAsync()); // a1 + the orphan both deleted outright
    }

    [Fact]
    public async Task Deleting_a_board_deletes_its_images_and_recycles_their_files()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1", "a2") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        string blob;
        await using (var db = _dbFactory.CreateDbContext())
            blob = _store.GetAbsolutePath(await db.Assets.Where(a => a.SourceId == "a1").Select(a => a.RelativePath).SingleAsync());
        Assert.True(File.Exists(blob));

        var recycler = new FakeRecycler();
        var removed = await new CurationService(_dbFactory, _store, recycler).DeleteBoardAsync(boardId);

        Assert.Equal(2, removed);
        Assert.Equal(2, recycler.Recycled.Count);                         // both files to the recycle bin
        Assert.Contains(blob, recycler.Recycled);
        Assert.False(File.Exists(blob));                                  // gone from the store
        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Empty(await db.Collections.ToListAsync());             // board grouping gone
            Assert.Equal(0, await db.CollectionItems.CountAsync());       // links cascaded away
            Assert.Equal(0, await db.Assets.CountAsync());                // image rows GONE (not tombstoned)
            Assert.Equal(2, await db.ArchiveOps.CountAsync(o => o.Kind == Hoard.Core.Sync.ArchiveOpKinds.AssetRemoved));
        }
    }

    [Fact]
    public async Task GetBoardSourceUrls_returns_the_boards_distinct_syncable_urls()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "b1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        var urls = await new LibraryService(_dbFactory, _store).GetBoardSourceUrlsAsync(boardId);

        Assert.Equal(2, urls.Count); // one syncable URL per merged source
        Assert.Contains("https://pinterest.com/jane/animals/", urls);
        Assert.Contains("https://pinterest.com/jane/plants/", urls);
    }

    [Fact]
    public async Task Known_items_span_every_merged_source_so_re_crawl_skips_held_pins()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        await new IngestService(_dbFactory, _store, new[] { Connector("board-B", "Plants", "p1") })
            .ImportAsync("https://pinterest.com/jane/plants/", new ConnectorOptions(), null, boardId);

        // A third import: capture the known items handed to the connector.
        var spy = Connector("board-A", "Animals", "a2");
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        var boards = spy.LastOptions!.KnownItems!.Select(k => k.BoardId).Distinct().OrderBy(b => b);
        Assert.Equal(new[] { "board-A", "board-B" }, boards);
    }

    [Fact]
    public async Task A_target_import_does_not_skip_a_pin_thats_only_in_another_board()
    {
        // "shared" lands in board M1 (from source board-A). Now import board-A into a DIFFERENT board M2.
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "shared") });
        var m1 = await ingest.CreateBoardAsync("M1", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m1);

        var m2 = await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "shared") })
            .CreateBoardAsync("M2", "https://pinterest.com/jane/animals/");
        var spy = Connector("board-A", "Animals", "shared");
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m2);

        // The M2 import is NOT told it already has "shared" (it's only in M1), so a real connector would download
        // + link it into M2 instead of skipping it (the old global archive wrongly skipped it).
        Assert.DoesNotContain(spy.LastOptions!.KnownItems!, k => k.SourceId == "shared");

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.Assets.CountAsync());          // one shared asset (dedup by hash)
        Assert.Equal(2, await db.CollectionItems.CountAsync()); // now linked into BOTH boards
    }

    [Fact]
    public async Task A_full_sync_re_downloads_a_held_pin_whose_blob_went_missing()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        // The blob vanishes outside the app (drive damage, or a replica that never received the file).
        string relativePath;
        await using (var db = _dbFactory.CreateDbContext())
            relativePath = (await db.Assets.SingleAsync()).RelativePath;
        File.Delete(_store.GetAbsolutePath(relativePath));

        var spy = Connector("board-A", "Animals", "a1");
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        // The lost pin was NOT pre-skipped, so the sync re-delivered it and the blob is back on disk.
        Assert.DoesNotContain(spy.LastOptions!.KnownItems!, k => k.SourceId == "a1");
        Assert.True(File.Exists(_store.GetAbsolutePath(relativePath)));
        await using (var db2 = _dbFactory.CreateDbContext())
            Assert.Equal(1, await db2.Assets.CountAsync()); // repaired in place, not duplicated
    }

    /// <summary>
    /// A "sync all" plan is captured up front while the grid stays interactive, so a board can be deleted
    /// between planning and its turn. Importing into a target that no longer exists must fail loudly: the
    /// silent fallback is the auto-folder path, which would mint a NEW board per source and re-download the
    /// lot (the skip-archive is scoped to the subtree of an id that isn't there any more).
    /// </summary>
    [Fact]
    public async Task Importing_into_a_board_that_no_longer_exists_fails_instead_of_creating_one()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Collections.Remove(await db.Collections.SingleAsync(c => c.Id == boardId));
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId));

        await using var after = _dbFactory.CreateDbContext();
        Assert.Empty(await after.Collections.ToListAsync()); // no board conjured from the crawl
        Assert.Empty(await after.Assets.ToListAsync());
    }

    /// <summary>
    /// The counterpart to the two tests above, and the cost of the everyday delta being cheap: verifying held
    /// pins against disk means walking the whole store, which is what a delta exists to avoid — so it trusts
    /// the index instead, and repairing a lost file is the job of "Full sync" (or the tile's own re-download).
    /// Pinned so the split stays a decision rather than a regression.
    /// </summary>
    [Fact]
    public async Task A_delta_sync_trusts_the_index_and_leaves_a_missing_blob_to_a_full_sync()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        string relativePath;
        await using (var db = _dbFactory.CreateDbContext())
            relativePath = (await db.Assets.SingleAsync()).RelativePath;
        File.Delete(_store.GetAbsolutePath(relativePath));

        var spy = Connector("board-A", "Animals", "a1");
        await new IngestService(_dbFactory, _store, new[] { spy }).ImportAsync(
            ["https://pinterest.com/jane/animals/"], new ConnectorOptions(), null, boardId, ImportMode.Delta);

        // Pre-skipped despite the hole on disk — and the crawl was told to stop early, which is the whole point.
        Assert.Contains(spy.LastOptions!.KnownItems!, k => k.SourceId == "a1");
        Assert.NotNull(spy.LastOptions!.StopAfterConsecutiveKnown);
        Assert.False(spy.LastOptions!.IncludeSubCollections);
    }

    [Fact]
    public async Task A_full_sync_re_downloads_a_held_pin_whose_blob_is_truncated()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        // A crash-torn write from an old build left a short blob at the content address — existence
        // alone would trust it; the recorded byte length says otherwise.
        string relativePath;
        await using (var db = _dbFactory.CreateDbContext())
            relativePath = (await db.Assets.SingleAsync()).RelativePath;
        File.WriteAllText(_store.GetAbsolutePath(relativePath), "x");

        var spy = Connector("board-A", "Animals", "a1");
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        Assert.DoesNotContain(spy.LastOptions!.KnownItems!, k => k.SourceId == "a1");
        await using (var db2 = _dbFactory.CreateDbContext())
        {
            var asset = await db2.Assets.SingleAsync();
            Assert.Equal(new FileInfo(_store.GetAbsolutePath(asset.RelativePath)).Length, asset.Bytes); // intact again
        }
    }

    [Fact]
    public async Task Reimport_fills_in_a_sources_missing_url_so_it_becomes_syncable()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);
        // Simulate a v3-backfilled url-less source (board id but no URL).
        await using (var db = _dbFactory.CreateDbContext())
        {
            var src = await db.CollectionSources.SingleAsync();
            src.SourceUrl = "";
            await db.SaveChangesAsync();
        }
        Assert.Empty(await new LibraryService(_dbFactory, _store).GetBoardSourceUrlsAsync(boardId)); // not syncable yet

        // A later real import (the connector supplies the board URL) fills the URL in.
        await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a2") })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, boardId);

        Assert.Equal(new[] { "https://pinterest.com/jane/animals/" },
            await new LibraryService(_dbFactory, _store).GetBoardSourceUrlsAsync(boardId)); // now syncable
    }

    [Fact]
    public async Task Reimport_reattaches_an_orphaned_live_asset_by_its_sidecar_board()
    {
        // Import "a1" into M1, then orphan it (its board was deleted) — it stays live with no links, but its
        // sidecar still says it came from board-A.
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var m1 = await ingest.CreateBoardAsync("M1", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m1);
        int a1Id;
        await using (var db = _dbFactory.CreateDbContext())
        {
            a1Id = await db.Assets.Where(a => a.SourceId == "a1").Select(a => a.Id).SingleAsync();
            db.CollectionItems.RemoveRange(await db.CollectionItems.ToListAsync()); // board deleted → links cascade
            await db.SaveChangesAsync();
        }

        // Re-import board-A — but it now lists a DIFFERENT pin ("a2"); "a1" is no longer on the board so it's
        // never re-crawled. It must still come back into the target by its sidecar board id.
        var m2 = await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a2") })
            .CreateBoardAsync("M2", "https://pinterest.com/jane/animals/");
        await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a2") })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m2);

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.True(await db.CollectionItems.AnyAsync(ci => ci.CollectionId == m2 && ci.AssetId == a1Id)); // re-attached
            Assert.Null(await db.Assets.Where(a => a.Id == a1Id).Select(a => a.DeletedAt).SingleAsync());       // still live
        }
    }

    [Fact]
    public async Task Reimport_does_not_reattach_a_tombstoned_orphan()
    {
        // "a1" imported, then per-image deleted (blacklisted) AND its link removed — a tombstoned orphan.
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a1") });
        var m1 = await ingest.CreateBoardAsync("M1", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m1);
        int a1Id;
        await using (var db = _dbFactory.CreateDbContext())
            a1Id = await db.Assets.Select(a => a.Id).SingleAsync();
        await new CurationService(_dbFactory, _store).DeleteAssetAsync(a1Id, "blacklisted");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CollectionItems.RemoveRange(await db.CollectionItems.ToListAsync());
            await db.SaveChangesAsync();
        }

        var m2 = await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a2") })
            .CreateBoardAsync("M2", "https://pinterest.com/jane/animals/");
        await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "a2") })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m2);

        // The tombstoned orphan stays gone — re-attach only touches live assets.
        await using (var db = _dbFactory.CreateDbContext())
            Assert.False(await db.CollectionItems.AnyAsync(ci => ci.AssetId == a1Id));
    }

    [Fact]
    public async Task A_target_import_still_skips_tombstoned_pins_globally()
    {
        // "p1" lands in M1, then is per-image deleted (the intended blacklist). Re-import into a fresh board M2.
        var ingest = new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "p1") });
        var m1 = await ingest.CreateBoardAsync("M1", "https://pinterest.com/jane/animals/");
        await ingest.ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m1);
        int assetId;
        await using (var db = _dbFactory.CreateDbContext()) assetId = await db.Assets.Select(a => a.Id).SingleAsync();
        await new CurationService(_dbFactory, _store).DeleteAssetAsync(assetId, "blacklisted");

        var m2 = await new IngestService(_dbFactory, _store, new[] { Connector("board-A", "Animals", "p1") })
            .CreateBoardAsync("M2", "https://pinterest.com/jane/animals/");
        var spy = Connector("board-A", "Animals", "p1");
        await new IngestService(_dbFactory, _store, new[] { spy })
            .ImportAsync("https://pinterest.com/jane/animals/", new ConnectorOptions(), null, m2);

        Assert.Contains(spy.LastOptions!.KnownItems!, k => k.SourceId == "p1"); // tombstone offered globally
        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(0, await db.CollectionItems.CountAsync(ci => ci.CollectionId == m2)); // not re-added to M2
            Assert.NotNull(await db.Assets.Where(a => a.Id == assetId).Select(a => a.DeletedAt).SingleAsync()); // still tombstoned
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

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
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

        public ConnectorOptions? LastOptions { get; private set; }
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            LastOptions = options;
            var temp = Path.Combine(Path.GetTempPath(), "hoard-merge-dl", Guid.NewGuid().ToString("N"));
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
                        RawJson = $"{{\"board\":{{\"id\":\"{_boardId}\"}}}}", // sidecar provenance for orphan re-attach
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
