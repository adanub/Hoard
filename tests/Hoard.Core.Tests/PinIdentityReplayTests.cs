using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// Replay under the pin-keyed identity: new ops resolve by the payload's pin, LEGACY (pre-v9) ops by
/// their sha column; added is an upsert-by-pin so devices converge without uid aliasing; and a rebuilt
/// index derives provenance from legacy payload sidecars via the one shared parser.
/// </summary>
public class PinIdentityReplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-pinreplay-test", Guid.NewGuid().ToString("N"));

    public PinIdentityReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Legacy_ops_replay_by_sha_and_derive_provenance_from_the_payload_sidecar()
    {
        var factory = NewDb("legacy.db");
        var opsRoot = Path.Combine(_dir, "legacy-ops");

        // A pre-v9 segment, verbatim: sha-keyed ops, payloads with NO first-class provenance and NO
        // pin identity fields — exactly what an old build wrote. The sidecar rides in metadataJson.
        var sidecar = """{"pin_id":"p1","board":{"id":"board-77"},"section_id":"sec-9"}""";
        ArchiveSegments.Append(opsRoot, "old-dev",
        [
            new ArchiveOp
            {
                DeviceId = "old-dev", Seq = 1, Hlc = Hlc(1, "old-dev"), Kind = ArchiveOpKinds.AssetAdded,
                Sha256 = Sha('a'),
                PayloadJson = """{"relativePath":"aa/aa/one.jpg","kind":1,"bytes":10,"sourceConnector":"pinterest","sourceId":"p1","metadataJson":"{\"pin_id\":\"p1\",\"board\":{\"id\":\"board-77\"},\"section_id\":\"sec-9\"}","importedAt":"2026-01-01T00:00:00+00:00"}""",
            },
            new ArchiveOp
            {
                DeviceId = "old-dev", Seq = 2, Hlc = Hlc(2, "old-dev"), Kind = ArchiveOpKinds.AssetTombstoned,
                Sha256 = Sha('a'),
                PayloadJson = """{"note":"legacy delete","deletedAt":"2026-01-02T00:00:00+00:00"}""",
            },
        ]);

        await using var db = factory.CreateDbContext();
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

        var asset = Assert.Single(await db.Assets.ToListAsync());
        Assert.Equal("p1", asset.SourceId);
        Assert.Equal("board-77", asset.SourceBoardId);   // derived at replay via the shared parser
        Assert.Equal("sec-9", asset.SourceSectionId);
        Assert.Equal("legacy delete", asset.DeletionNote); // the sha-keyed tombstone found its row
        _ = sidecar; // (kept for readability of what the escaped payload above contains)
    }

    [Fact]
    public async Task Two_devices_that_saved_the_same_pin_converge_on_one_row()
    {
        var factory = NewDb("converge.db");
        var opsRoot = Path.Combine(_dir, "converge-ops");

        // Device A saved the pin; device B saved the SAME pin later, after the source re-encoded it
        // (different bytes). The natural pin key converges them — no uid minting, no aliasing; LWW by
        // HLC picks B's content.
        ArchiveSegments.Append(opsRoot, "dev-a", [AddedOp("dev-a", 1, hlcTick: 1, pin: "p1", sha: Sha('a'))]);
        ArchiveSegments.Append(opsRoot, "dev-b", [AddedOp("dev-b", 1, hlcTick: 5, pin: "p1", sha: Sha('b'))]);

        await using var db = factory.CreateDbContext();
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

        var asset = Assert.Single(await db.Assets.ToListAsync());
        Assert.Equal("p1", asset.SourceId);
        Assert.Equal(Sha('b'), asset.Sha256); // the later write won
    }

    [Fact]
    public async Task A_restore_whose_bytes_match_another_row_applies_cleanly()
    {
        var factory = NewDb("collide.db");
        var opsRoot = Path.Combine(_dir, "collide-ops");

        // Two pins with different content; then pin-1 is restored and its re-download yields bytes
        // IDENTICAL to pin-2's. The dedup era bailed out here (unique sha index); now both rows simply
        // share the blob pointer.
        ArchiveSegments.Append(opsRoot, "dev",
        [
            AddedOp("dev", 1, hlcTick: 1, pin: "p1", sha: Sha('a')),
            AddedOp("dev", 2, hlcTick: 2, pin: "p2", sha: Sha('b')),
            new ArchiveOp
            {
                DeviceId = "dev", Seq = 3, Hlc = Hlc(3, "dev"), Kind = ArchiveOpKinds.AssetRestored,
                Sha256 = Sha('a'),
                PayloadJson = ArchiveOpJson.Serialize(new AssetContentChangedPayload(
                    Sha('b'), $"bb/bb/{Sha('b')}.jpg", 10, "pinterest", "p1")),
            },
        ]);

        await using var db = factory.CreateDbContext();
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

        var assets = await db.Assets.OrderBy(a => a.SourceId).ToListAsync();
        Assert.Equal(2, assets.Count);
        Assert.All(assets, a => Assert.Equal(Sha('b'), a.Sha256)); // both rows now point at the same blob
    }

    [Fact]
    public async Task A_stale_op_pulled_late_does_not_regress_newer_state()
    {
        // Device A refreshed the pin (hlc 9, already applied); device B's ORIGINAL import (hlc 1)
        // arrives only later — as the sole pending op. Without the per-row LWW register this would be
        // last-applied-wins and the row would regress to January.
        var factory = NewDb("stale.db");
        var opsRoot = Path.Combine(_dir, "stale-ops");
        ArchiveSegments.Append(opsRoot, "dev-a", [AddedOp("dev-a", 1, hlcTick: 9, pin: "p1", sha: Sha('a'))]);

        await using var db = factory.CreateDbContext();
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));
        Assert.Equal(Sha('a'), (await db.Assets.SingleAsync()).Sha256);

        ArchiveSegments.Append(opsRoot, "dev-b", [AddedOp("dev-b", 1, hlcTick: 1, pin: "p1", sha: Sha('b'))]);
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

        var asset = Assert.Single(await db.Assets.ToListAsync());
        Assert.Equal(Sha('a'), asset.Sha256); // the newer state stood; the stale op was dropped
    }

    [Fact]
    public async Task A_pinless_op_never_captures_a_pinned_rows_identity()
    {
        // Pin p1 exists; a pinless item with IDENTICAL bytes arrives from another device. The sha
        // fallback must not hand p1's row to the anonymous item (that would null its SourceId and
        // orphan every later pin-keyed op).
        var factory = NewDb("pinless.db");
        var opsRoot = Path.Combine(_dir, "pinless-ops");
        ArchiveSegments.Append(opsRoot, "dev",
        [
            AddedOp("dev", 1, hlcTick: 1, pin: "p1", sha: Sha('a')),
            new ArchiveOp
            {
                DeviceId = "dev", Seq = 2, Hlc = Hlc(2, "dev"), Kind = ArchiveOpKinds.AssetAdded,
                Sha256 = Sha('a'),
                PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
                    $"aa/bb/{Sha('a')}.jpg", "image/jpeg", MediaKind.Image, 10, 10, 10,
                    "pinterest", null, null, null, "anonymous", null, null,
                    null, DateTimeOffset.UnixEpoch.AddMinutes(2), null)),
            },
        ]);

        await using var db = factory.CreateDbContext();
        await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

        var assets = await db.Assets.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, assets.Count);                    // the anonymous item became its OWN row
        Assert.Equal("p1", assets[0].SourceId);           // the pin kept its identity
        Assert.Null(assets[1].SourceId);
    }

    private TestDbContextFactory NewDb(string name)
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, name));
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        return factory;
    }

    private static ArchiveOp AddedOp(string device, long seq, int hlcTick, string pin, string sha) => new()
    {
        DeviceId = device,
        Seq = seq,
        Hlc = Hlc(hlcTick, device),
        Kind = ArchiveOpKinds.AssetAdded,
        Sha256 = sha,
        PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
            $"aa/bb/{sha}.jpg", "image/jpeg", MediaKind.Image, 10, 10, 10,
            "pinterest", pin, null, null, pin, null, null,
            null, DateTimeOffset.UnixEpoch.AddMinutes(hlcTick), null, "board-1")),
    };

    private static string Hlc(int tick, string device) => $"{tick:D14}-000000-{device}";

    private static string Sha(char c) => new(c, 64);
}
