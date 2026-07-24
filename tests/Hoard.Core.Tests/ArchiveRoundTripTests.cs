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
/// The SYNC-DESIGN P1 proof: the archive op log alone must carry the whole metadata database. Both the
/// synthesised history of a pre-op database and the ops emitted live by the real services are rebuilt
/// into a fresh database and compared for equivalence.
/// </summary>
public class ArchiveRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-roundtrip-test", Guid.NewGuid().ToString("N"));

    public ArchiveRoundTripTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Synthesised_ops_rebuild_an_equivalent_database()
    {
        var source = CreateDb("source.db");

        // A realistic pre-op project: two boards (one renamed), a section folder, two merged sources,
        // live + tombstoned assets, links with and without per-source attribution, a tag.
        await using (var db = source.CreateDbContext())
        {
            var t = DateTimeOffset.UtcNow.AddDays(-30);
            var alpha = new Collection { Name = "Alpha", SourceConnector = "pinterest", SourceBoardId = "bA", SourceUrl = "https://p/bA", CreatedAt = t };
            var folder = new Collection { Name = "Kitchen", SourceConnector = "pinterest", Parent = alpha, SourceSectionId = "s1", CreatedAt = t.AddMinutes(1) };
            var beta = new Collection { Name = "Beta", DisplayName = "My Beta", SourceConnector = "pinterest", CreatedAt = t.AddMinutes(2) };
            db.Collections.AddRange(alpha, folder, beta);

            var s1 = new CollectionSource { Collection = alpha, SourceConnector = "pinterest", SourceBoardId = "bA", SourceUrl = "https://p/bA", Name = "Alpha", AddedAt = t.AddMinutes(3) };
            var s2 = new CollectionSource { Collection = alpha, SourceConnector = "pinterest", SourceBoardId = "bX", SourceUrl = "https://p/bX", Name = "Extra", AddedAt = t.AddMinutes(4) };
            db.CollectionSources.AddRange(s1, s2);

            var tag = new Tag { Name = "trees" };
            var a1 = MakeAsset("sha-1", t.AddMinutes(5));
            a1.AssetTags.Add(new AssetTag { Asset = a1, Tag = tag });
            var a2 = MakeAsset("sha-2", t.AddMinutes(6));
            var a3 = MakeAsset("sha-3", t.AddMinutes(7));
            a3.DeletedAt = t.AddMinutes(8);
            a3.DeletionNote = "duplicate of another pin";
            db.Assets.AddRange(a1, a2, a3);

            db.CollectionItems.AddRange(
                new CollectionItem { Collection = alpha, Asset = a1, CollectionSource = s1, Note = "n1", AddedAt = t.AddMinutes(9) },
                new CollectionItem { Collection = folder, Asset = a2, AddedAt = t.AddMinutes(10) },
                new CollectionItem { Collection = beta, Asset = a2, AddedAt = t.AddMinutes(11) },
                new CollectionItem { Collection = alpha, Asset = a3, CollectionSource = s2, AddedAt = t.AddMinutes(12) });
            await db.SaveChangesAsync();
        }

        // Synthesise the initial log: 3 created + 1 renamed + 2 attached + 3 added + 1 tombstoned + 4 linked.
        await using (var db = source.CreateDbContext())
        {
            var emitted = await ArchiveOpSynthesiser.SynthesiseAsync(db, deviceId: "migrator");
            Assert.Equal(14, emitted);
            await db.SaveChangesAsync();
        }

        // Coverage-aware: a second run finds everything already described and emits nothing.
        await using (var db = source.CreateDbContext())
            Assert.Equal(0, await ArchiveOpSynthesiser.SynthesiseAsync(db, deviceId: "migrator"));

        await AssertRebuildMatchesAsync(source, "rebuilt-synth.db");
    }

    [Fact]
    public async Task Live_service_ops_rebuild_an_equivalent_database()
    {
        var factory = CreateDb("live.db");
        var store = new ContentAddressedStore(Path.Combine(_dir, "store"));
        var archive = new ArchiveLog("live-device"); // shared across services, like the app's DI singleton
        var ingest = new IngestService(factory, store, new[] { new RoundTripConnector() }, null, archive);
        var curation = new CurationService(factory, store, null, archive);

        // The real pipeline end to end: import two boards, rename, tombstone, file into a folder, delete a board.
        await ingest.ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);

        int natureId, cityId, asset1Id, asset2Id;
        await using (var db = factory.CreateDbContext())
        {
            natureId = (await db.Collections.SingleAsync(c => c.Name == "Nature")).Id;
            cityId = (await db.Collections.SingleAsync(c => c.Name == "City")).Id;
            asset1Id = (await db.CollectionItems.Where(ci => ci.CollectionId == natureId).Select(ci => ci.Asset).OrderBy(a => a.Id).FirstAsync()).Id;
            asset2Id = (await db.CollectionItems.Where(ci => ci.CollectionId == natureId).Select(ci => ci.Asset).OrderBy(a => a.Id).LastAsync()).Id;
        }

        await curation.RenameBoardAsync(natureId, "My Nature");
        await curation.DeleteAssetAsync(asset1Id, "blurry");
        var folderId = await ingest.CreateBoardAsync("Favourites", "https://pinterest.com/jane/", parentId: natureId);
        await curation.MoveAssetWithinBoardAsync(asset2Id, natureId, folderId);
        await curation.DeleteBoardAsync(cityId);

        await AssertRebuildMatchesAsync(factory, "rebuilt-live.db");
    }

    // ---- harness ---------------------------------------------------------------------------------

    private TestDbContextFactory CreateDb(string name)
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, name));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        return factory;
    }

    private async Task AssertRebuildMatchesAsync(TestDbContextFactory source, string targetName)
    {
        var target = CreateDb(targetName);
        List<ArchiveOp> ops;
        await using (var db = source.CreateDbContext())
            ops = await db.ArchiveOps.AsNoTracking().ToListAsync();
        await using (var db = target.CreateDbContext())
            await ArchiveRebuilder.RebuildAsync(db, ops);

        var expected = await ArchiveTestProjection.ProjectAsync(source);
        var actual = await ArchiveTestProjection.ProjectAsync(target);
        var dump = Environment.GetEnvironmentVariable("HOARD_ROUNDTRIP_DUMP");
        if (dump is not null && expected != actual)
        {
            File.WriteAllText(Path.Combine(dump, $"{targetName}.expected.txt"), expected);
            File.WriteAllText(Path.Combine(dump, $"{targetName}.actual.txt"), actual);
            File.WriteAllText(Path.Combine(dump, $"{targetName}.ops.txt"), string.Join("\n",
                ops.OrderBy(o => o.Hlc, StringComparer.Ordinal)
                   .Select(o => $"{o.Hlc}|{o.Seq}|{o.Kind}|sha={o.Sha256}|uid={o.EntityUid}|{o.PayloadJson}")));
        }
        Assert.Equal(expected, actual);
    }

    private static Asset MakeAsset(string sha, DateTimeOffset importedAt) => new()
    {
        Sha256 = sha,
        RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
        MimeType = "image/jpeg",
        Kind = MediaKind.Image,
        Width = 800,
        Height = 600,
        Bytes = 1234,
        SourceConnector = "pinterest",
        SourceId = $"pin-{sha}",
        SourceUrl = $"https://i.pinimg.com/{sha}.jpg",
        Title = $"Title {sha}",
        Description = "A description",
        MetadataJson = "{\"id\":\"pin-" + sha + "\",\"board\":{\"id\":\"bA\"}}",
        ImportedAt = importedAt,
    };

    private sealed class RoundTripConnector : ISourceConnector
    {
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hoard-roundtrip-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var pins = new[] { ("AAA", "Nature"), ("BBB", "Nature"), ("CCC", "City") };
                for (var i = 0; i < pins.Length; i++)
                {
                    var (content, board) = pins[i];
                    var file = Path.Combine(temp, $"{i}_{content}.jpg");
                    File.WriteAllText(file, content);
                    await onItem(new SourceMediaItem
                    {
                        FilePath = file,
                        Connector = Name,
                        SourceId = $"pin{i}",
                        BoardName = board,
                        BoardId = board,
                        BoardUrl = $"https://pinterest.com/jane/{board.ToLowerInvariant()}/",
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

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
