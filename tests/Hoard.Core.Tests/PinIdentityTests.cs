using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Metadata;
using Hoard.Core.Storage;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The pin-keyed identity model: an Asset is one saved pin — (connector, SourceId) — with the content
/// sha as a shared blob pointer. One rule replaces the old cooperating four: the pin-keyed upsert
/// (re-crawls update in place), first-class provenance drives the skip-archive and orphan re-attach,
/// tombstones are per-pin, and blobs are freed only with their last live referrer.
/// </summary>
public class PinIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-pinid-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;
    private readonly ContentAddressedStore _store;

    public PinIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
        using (var db = _dbFactory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new ContentAddressedStore(Path.Combine(_dir, "store"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── ingest: the pin-keyed upsert ──────────────────────────────────────────────

    [Fact]
    public async Task A_reencoded_pin_updates_its_row_in_place_and_reemits_added()
    {
        var boardUrl = "https://pinterest.com/jane/nature/";
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "v1-bytes", "board-A")) })
            .ImportAsync(boardUrl, new ConnectorOptions(), null);
        // The source re-encoded the pin: same pin id, new bytes.
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "v2-bytes", "board-A")) })
            .ImportAsync(boardUrl, new ConnectorOptions(), null);

        await using var db = _dbFactory.CreateDbContext();
        var asset = Assert.Single(await db.Assets.ToListAsync()); // still ONE row — the pin
        Assert.Equal("v2-bytes", await File.ReadAllTextAsync(_store.GetAbsolutePath(asset.RelativePath)));
        Assert.Equal(1, await db.CollectionItems.CountAsync());   // and one link
        // The refresh propagates as a re-emitted added op (replayed as an upsert on other machines).
        Assert.Equal(2, await db.ArchiveOps.CountAsync(o => o.Kind == ArchiveOpKinds.AssetAdded));
    }

    [Fact]
    public async Task Provenance_lands_on_the_row_at_import()
    {
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "bytes", "board-A")) })
            .ImportAsync("https://pinterest.com/jane/nature/", new ConnectorOptions(), null);

        await using var db = _dbFactory.CreateDbContext();
        var asset = Assert.Single(await db.Assets.ToListAsync());
        Assert.Equal("board-A", asset.SourceBoardId);
    }

    // ── tombstones are per-pin; blobs are freed with their LAST live referrer ─────

    [Fact]
    public async Task Tombstoning_one_pin_spares_a_shared_blob_until_the_last_referrer_goes()
    {
        // Two pins, identical bytes: two rows, one blob.
        await new IngestService(_dbFactory, _store,
                new[] { new PinConnector(("p1", "same-bytes", "board-A"), ("p2", "same-bytes", "board-B")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        int p1Id, p2Id;
        string blobPath;
        await using (var db = _dbFactory.CreateDbContext())
        {
            p1Id = await db.Assets.Where(a => a.SourceId == "p1").Select(a => a.Id).SingleAsync();
            p2Id = await db.Assets.Where(a => a.SourceId == "p2").Select(a => a.Id).SingleAsync();
            blobPath = _store.GetAbsolutePath(await db.Assets.Where(a => a.Id == p1Id)
                .Select(a => a.RelativePath).SingleAsync());
        }

        var curation = new CurationService(_dbFactory, _store);
        Assert.NotNull(await curation.DeleteAssetAsync(p1Id, "dupe"));
        Assert.True(File.Exists(blobPath)); // p2 still points at these bytes — NOT freed

        Assert.NotNull(await curation.DeleteAssetAsync(p2Id, "and the other"));
        Assert.False(File.Exists(blobPath)); // last live referrer gone — now freed

        await using (var db = _dbFactory.CreateDbContext())
            Assert.Equal(2, await db.Assets.CountAsync(a => a.DeletedAt != null)); // both rows kept as tombstones
    }

    [Fact]
    public async Task A_tombstone_skip_refrees_the_reimported_blob_unless_a_live_pin_shares_it()
    {
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "bytes-1", "board-A")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        int p1Id;
        string blobPath;
        await using (var db = _dbFactory.CreateDbContext())
        {
            var a = await db.Assets.SingleAsync();
            p1Id = a.Id;
            blobPath = _store.GetAbsolutePath(a.RelativePath);
        }
        await new CurationService(_dbFactory, _store).DeleteAssetAsync(p1Id, "gone");
        Assert.False(File.Exists(blobPath));

        // The crawl re-lists the tombstoned pin (the fake ignores the skip-archive): the DB re-check
        // skips it AND re-frees the blob the download re-staged.
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "bytes-1", "board-A")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        Assert.False(File.Exists(blobPath));
        await using (var db = _dbFactory.CreateDbContext())
        {
            var row = Assert.Single(await db.Assets.ToListAsync());
            Assert.NotNull(row.DeletedAt); // never resurrected
        }
    }

    // ── the skip-archive rides the pin's own provenance ───────────────────────────

    [Fact]
    public async Task Known_items_come_from_the_pins_own_board_and_tombstones_need_no_links()
    {
        await new IngestService(_dbFactory, _store, new[] { new PinConnector(("p1", "held-bytes", "board-A")) })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        // A linkless tombstone (its board is long gone): the old link-joined blacklist missed these.
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Assets.Add(new Asset
            {
                Sha256 = new string('e', 64), RelativePath = "ee/ee/gone.jpg",
                SourceConnector = "pinterest", SourceId = "p9", SourceBoardId = "board-B",
                ImportedAt = DateTimeOffset.UtcNow, DeletedAt = DateTimeOffset.UtcNow, DeletionNote = "x",
            });
            await db.SaveChangesAsync();
        }

        var probe = new PinConnector();
        await new IngestService(_dbFactory, _store, new[] { probe })
            .ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        var known = probe.LastOptions!.KnownItems!;
        Assert.Contains(new KnownSourceItem("board-A", "p1"), known); // held live pin, under its OWN board
        Assert.Contains(new KnownSourceItem("board-B", "p9"), known); // linkless tombstone still blacklists
    }

    // ── orphan recovery from first-class provenance ───────────────────────────────

    [Fact]
    public async Task An_empty_crawl_still_reattaches_orphans_of_a_recorded_source()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { new PinConnector() }); // emits NOTHING
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/gone/");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CollectionSources.Add(new CollectionSource
            {
                CollectionId = boardId, SourceConnector = "pinterest", SourceBoardId = "board-X",
                SourceUrl = "https://pinterest.com/jane/gone/", AddedAt = DateTimeOffset.UtcNow,
            });
            // The orphan: restored content whose pin no longer exists at the source — recoverable purely
            // from its stored provenance (the board is EMPTY at the source, so no crawl ever re-lists it).
            db.Assets.Add(new Asset
            {
                Sha256 = new string('a', 64), RelativePath = "aa/aa/orphan.jpg",
                SourceConnector = "pinterest", SourceId = "p5", SourceBoardId = "board-X",
                ImportedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await ingest.ImportAsync("https://pinterest.com/jane/gone/", new ConnectorOptions(), null, boardId);

        await using (var db = _dbFactory.CreateDbContext())
        {
            var link = Assert.Single(await db.CollectionItems.ToListAsync());
            Assert.Equal(boardId, link.CollectionId);
            Assert.NotNull(link.CollectionSourceId); // attributed to the recorded source
        }
    }

    [Fact]
    public async Task A_sectioned_orphan_refiles_into_its_section_folder()
    {
        var ingest = new IngestService(_dbFactory, _store, new[] { new PinConnector() });
        var boardId = await ingest.CreateBoardAsync("My board", "https://pinterest.com/jane/gone/");
        var folderId = await ingest.CreateBoardAsync("Kitchen", "", parentId: boardId, sectionId: "sec-1");
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.CollectionSources.Add(new CollectionSource
            {
                CollectionId = boardId, SourceConnector = "pinterest", SourceBoardId = "board-X",
                SourceUrl = "https://pinterest.com/jane/gone/", AddedAt = DateTimeOffset.UtcNow,
            });
            db.Assets.Add(new Asset
            {
                Sha256 = new string('b', 64), RelativePath = "bb/bb/orphan.jpg",
                SourceConnector = "pinterest", SourceId = "p6",
                SourceBoardId = "board-X", SourceSectionId = "sec-1",
                ImportedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await ingest.ImportAsync("https://pinterest.com/jane/gone/", new ConnectorOptions(), null, boardId);

        await using (var db = _dbFactory.CreateDbContext())
        {
            var link = Assert.Single(await db.CollectionItems.ToListAsync());
            Assert.Equal(folderId, link.CollectionId); // its section folder, not the board root
        }
    }

    /// <summary>A connector serving (pin id, content, board id) triples; empty = a crawl that lists nothing.
    /// Ignores the skip-archive (like a connector whose archive was lost) so DB-side rules are exercised.</summary>
    private sealed class PinConnector : ISourceConnector
    {
        private readonly (string Pin, string Content, string Board)[] _items;
        public PinConnector(params (string, string, string)[] items) => _items = items;

        public ConnectorOptions? LastOptions { get; private set; }
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            LastOptions = options;
            var temp = Path.Combine(Path.GetTempPath(), "hoard-pinid-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var (pin, content, board) in _items)
                {
                    var file = Path.Combine(temp, $"{pin}.jpg");
                    await File.WriteAllTextAsync(file, content, ct);
                    await onItem(new SourceMediaItem
                    {
                        FilePath = file,
                        Connector = Name,
                        SourceId = pin,
                        BoardId = board,
                        BoardName = board,
                        BoardUrl = url,
                        Title = pin,
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
