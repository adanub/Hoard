using Hoard.Core.Domain;
using Hoard.Core.Connectors;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Storage;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The SYNC-DESIGN P2 proof: two machines sharing one project folder converge through the per-device op
/// segments alone — each writes only its own <c>ops/&lt;deviceId&gt;.jsonl</c>, each catches up the
/// other's — plus the segment format's crash-safety contract (torn tails are repaired and re-landed).
/// </summary>
public class ArchiveConvergenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-converge-test", Guid.NewGuid().ToString("N"));
    private string OpsRoot => Path.Combine(_dir, "ops");

    public ArchiveConvergenceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Two_devices_converge_via_shared_op_segments()
    {
        // One shared project folder (ops dir + blob store); each "machine" has its own database.
        var store = new ContentAddressedStore(Path.Combine(_dir, "store"));
        var machineA = Machine("machine-a", "a.db", store);
        var machineB = Machine("machine-b", "b.db", store);

        // A imports (flushes its segment at import end); B opens the shared folder and catches up.
        await machineA.Ingest.ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        await SyncAsync(machineB);

        Assert.Equal(
            await ArchiveTestProjection.ProjectAsync(machineA.Db),
            await ArchiveTestProjection.ProjectAsync(machineB.Db));

        // B curates — a rename and a tombstone — then A catches up B's segment.
        int natureId, tombstoneId;
        await using (var db = machineB.Db.CreateDbContext())
        {
            natureId = (await db.Collections.SingleAsync(c => c.Name == "Nature")).Id;
            tombstoneId = (await db.CollectionItems.Where(ci => ci.CollectionId == natureId)
                .Select(ci => ci.AssetId).OrderBy(id => id).FirstAsync());
        }
        await machineB.Curation.RenameBoardAsync(natureId, "Nature (curated)");
        await machineB.Curation.DeleteAssetAsync(tombstoneId, "not wanted");
        await SyncAsync(machineA);

        var a = await ArchiveTestProjection.ProjectAsync(machineA.Db);
        Assert.Equal(a, await ArchiveTestProjection.ProjectAsync(machineB.Db));
        Assert.Contains("Nature (curated)", a);
        Assert.Contains("not wanted", a);

        // Watermarks: everything is already applied, so another catch-up is a no-op on both sides.
        await using (var db = machineA.Db.CreateDbContext())
            Assert.Equal(0, await ArchiveSync.CatchUpAsync(db, OpsRoot, machineA.Archive));
        await using (var db = machineB.Db.CreateDbContext())
            Assert.Equal(0, await ArchiveSync.CatchUpAsync(db, OpsRoot, machineB.Archive));

        // One writer per segment: each file holds exactly its own device's ops, mirroring that device's
        // rows in the (now merged) tables.
        foreach (var machine in new[] { machineA, machineB })
        {
            var segment = ArchiveSegments.Read(ArchiveSegments.SegmentPath(OpsRoot, machine.Archive.DeviceId), machine.Archive.DeviceId);
            Assert.NotEmpty(segment);
            Assert.All(segment, op => Assert.Equal(machine.Archive.DeviceId, op.DeviceId));
            await using var db = machineA.Db.CreateDbContext();
            var tableOps = await db.ArchiveOps.Where(o => o.DeviceId == machine.Archive.DeviceId)
                .OrderBy(o => o.Seq).AsNoTracking().ToListAsync();
            Assert.Equal(tableOps.Count, segment.Count);
            // Payloads compare semantically: the segment writer re-encodes the JSON (escaping may
            // normalise), and the contract is JSON equality, not byte equality.
            Assert.Equal(tableOps.Select(o => (o.Seq, o.Hlc, o.Kind, o.Sha256, o.EntityUid, Canon(o.PayloadJson))),
                segment.Select(o => (o.Seq, o.Hlc, o.Kind, o.Sha256, o.EntityUid, Canon(o.PayloadJson))));
        }
    }

    [Fact]
    public async Task Catch_up_applies_cross_device_dependencies_regardless_of_device_id_order()
    {
        // Device ids chosen so the DEPENDENT device's segment sorts FIRST: "a-curator" renames a board
        // that "z-creator" created. A per-device apply order would hit the rename before the board exists,
        // drop it silently, and advance the watermark past it forever — the merged (HLC-ordered) apply
        // must land it. A fresh third machine is the acid test: it sees both segments only.
        var store = new ContentAddressedStore(Path.Combine(_dir, "store"));
        var creator = Machine("z-creator", "z.db", store);
        var curator = Machine("a-curator", "a.db", store);

        await creator.Ingest.ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
        await SyncAsync(curator);
        int boardId;
        await using (var db = curator.Db.CreateDbContext())
            boardId = (await db.Collections.SingleAsync(c => c.Name == "Nature")).Id;
        await curator.Curation.RenameBoardAsync(boardId, "Nature (renamed)");

        var fresh = Machine("m-fresh", "m.db", store);
        await SyncAsync(fresh);

        var view = await ArchiveTestProjection.ProjectAsync(fresh.Db);
        Assert.Contains("Nature (renamed)", view);
        await SyncAsync(creator);
        Assert.Equal(await ArchiveTestProjection.ProjectAsync(creator.Db), view);
    }

    [Fact]
    public async Task Seq_seeding_consults_the_segment_so_a_failed_catch_up_cannot_reuse_seqs()
    {
        // A fresh/wiped index whose open-time catch-up failed: the table is empty but the segment holds
        // this device's history. New ops must mint seqs beyond the file, or they'd collide with it and
        // silently never flush.
        var factory = CreateDb("seed.db");
        var first = new ArchiveLog("device-seed", opsRoot: () => OpsRoot);
        await using (var db = factory.CreateDbContext())
        {
            await first.EnsureReadyAsync(db);
            first.RecordAssetRemoved(db, Doc("sha-1"));
            first.RecordAssetRemoved(db, Doc("sha-2"));
            await db.SaveChangesAsync();
            await first.FlushSegmentAsync(db);
        }

        // Same device, EMPTY table (the wiped index), NO catch-up run — straight to a new local op.
        var wiped = CreateDb("seed-wiped.db");
        var restarted = new ArchiveLog("device-seed", opsRoot: () => OpsRoot);
        await using (var db = wiped.CreateDbContext())
        {
            await restarted.EnsureReadyAsync(db);
            restarted.RecordAssetRemoved(db, Doc("sha-3"));
            await db.SaveChangesAsync();
            Assert.Equal(3, (await db.ArchiveOps.SingleAsync()).Seq); // beyond the segment's 1..2
        }
    }

    [Fact]
    public async Task Torn_tail_is_repaired_and_the_op_relands_on_the_next_flush()
    {
        var factory = CreateDb("torn.db");
        var archive = new ArchiveLog("device-torn", opsRoot: () => OpsRoot);
        var path = ArchiveSegments.SegmentPath(OpsRoot, "device-torn");

        await using (var db = factory.CreateDbContext())
        {
            await archive.EnsureReadyAsync(db);
            archive.RecordAssetRemoved(db, Doc("sha-1"));
            archive.RecordAssetRemoved(db, Doc("sha-2"));
            await db.SaveChangesAsync();
            await archive.FlushSegmentAsync(db);
        }
        Assert.Equal(2, ArchiveSegments.Read(path, "device-torn").Count);

        // A crash mid-append leaves a partial line with no trailing newline; readers must stop before it.
        File.AppendAllText(path, "{\"seq\":3,\"hlc\":\"trunc");
        Assert.Equal(2, ArchiveSegments.Read(path, "device-torn").Count);

        // The next flush repairs the tail and appends cleanly — no welded garbage line, nothing lost.
        await using (var db = factory.CreateDbContext())
        {
            archive.RecordAssetRemoved(db, Doc("sha-3"));
            await db.SaveChangesAsync();
            await archive.FlushSegmentAsync(db);
        }
        var ops = ArchiveSegments.Read(path, "device-torn");
        Assert.Equal([1L, 2L, 3L], ops.Select(o => o.Seq));
    }

    [Fact]
    public async Task Flush_is_idempotent_and_survives_a_restart()
    {
        var factory = CreateDb("flush.db");
        var archive = new ArchiveLog("device-flush", opsRoot: () => OpsRoot);
        var path = ArchiveSegments.SegmentPath(OpsRoot, "device-flush");

        await using (var db = factory.CreateDbContext())
        {
            await archive.EnsureReadyAsync(db);
            archive.RecordAssetRemoved(db, Doc("sha-1"));
            await db.SaveChangesAsync();
            await archive.FlushSegmentAsync(db);
            await archive.FlushSegmentAsync(db); // nothing new — nothing appended
        }
        Assert.Single(ArchiveSegments.Read(path, "device-flush"));

        // A fresh ArchiveLog (an app restart) re-derives the watermark from the file, not memory.
        var restarted = new ArchiveLog("device-flush", opsRoot: () => OpsRoot);
        await using (var db = factory.CreateDbContext())
        {
            await restarted.EnsureReadyAsync(db);
            await restarted.FlushSegmentAsync(db);
        }
        Assert.Single(ArchiveSegments.Read(path, "device-flush"));
    }

    private static string? Canon(string? json) =>
        json is null ? null : System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(json).RootElement);

    // ---- harness ---------------------------------------------------------------------------------

    private sealed record TestMachine(TestDbContextFactory Db, ArchiveLog Archive, IngestService Ingest, CurationService Curation);

    private TestMachine Machine(string deviceId, string dbName, ContentAddressedStore store)
    {
        var factory = CreateDb(dbName);
        var archive = new ArchiveLog(deviceId, opsRoot: () => OpsRoot);
        return new TestMachine(
            factory, archive,
            new IngestService(factory, store, new[] { new FakePins() }, null, archive),
            new CurationService(factory, store, null, archive));
    }

    /// <summary>What the app does at open: backfill the machine's own segment, catch up foreign ones.</summary>
    private async Task SyncAsync(TestMachine machine)
    {
        await using var db = machine.Db.CreateDbContext();
        await ArchiveSync.SyncAtOpenAsync(db, OpsRoot, machine.Archive);
    }

    private TestDbContextFactory CreateDb(string name)
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, name));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        return factory;
    }

    private sealed class FakePins : ISourceConnector
    {
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hoard-converge-dl", Guid.NewGuid().ToString("N"));
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

    /// <summary>A minimal asset row for tests that only need SOME op to exercise segment mechanics
    /// (pinless, so the op keys on its sha — the legacy fallback path).</summary>
    private static Asset Doc(string sha) => new() { Sha256 = sha };
}
