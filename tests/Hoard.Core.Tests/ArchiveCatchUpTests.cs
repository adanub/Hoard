using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// Resilience of <see cref="ArchiveSync.CatchUpAsync"/>'s apply loop (the P4 review findings): pending
/// detection is a per-device set difference (a hole behind a committed higher seq re-pends and heals),
/// and one unappliable op is skipped-and-remembered instead of poisoning its whole batch.
/// </summary>
public class ArchiveCatchUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-catchup-test", Guid.NewGuid().ToString("N"));

    public ArchiveCatchUpTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task A_hole_behind_a_committed_higher_seq_repends_and_heals()
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, "hole.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var opsRoot = Path.Combine(_dir, "hole-ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 5).Select(AssetAddedOp));

        await using (var db = factory.CreateDbContext())
            Assert.Equal(5, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host")));

        // Simulate a rolled-back batch: ops 2–4 (rows AND effects) gone, while 1 and 5 stand committed —
        // exactly the state a crash mid-catch-up leaves once a later op has landed beyond the hole.
        await using (var db = factory.CreateDbContext())
        {
            db.ArchiveOps.RemoveRange(await db.ArchiveOps.Where(o => o.Seq >= 2 && o.Seq <= 4).ToListAsync());
            db.Assets.RemoveRange(await db.Assets.Where(a => a.Sha256 != Sha(1) && a.Sha256 != Sha(5)).ToListAsync());
            await db.SaveChangesAsync();
        }

        // A MAX(Seq) watermark would see 5 and skip everything; the set difference re-pends the hole.
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(3, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host")));
            Assert.Equal(5, await db.Assets.CountAsync());
            Assert.Equal(0, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host")));
        }
    }

    [Fact]
    public async Task An_unappliable_op_is_skipped_without_poisoning_its_batch()
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, "poison.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var opsRoot = Path.Combine(_dir, "poison-ops");

        // Op 2 has a valid envelope but no payload — parses from the segment fine, throws in apply.
        var poison = AssetAddedOp(2);
        poison.PayloadJson = null;
        ArchiveSegments.Append(opsRoot, "dev", [AssetAddedOp(1), poison, AssetAddedOp(3)]);

        await using (var db = factory.CreateDbContext())
        {
            await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"));

            // The good ops applied; the poison op's row is recorded (the skip is remembered) with no effect.
            Assert.Equal(2, await db.Assets.CountAsync());
            Assert.Equal(3, await db.ArchiveOps.CountAsync());
            Assert.Equal(0, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host"))); // not retried
        }
    }

    private static ArchiveOp AssetAddedOp(int n) => new()
    {
        DeviceId = "dev",
        Seq = n,
        Hlc = $"{n:D14}-000000-dev", // the real fixed-width HLC shape: {unixMs:D14}-{counter:D6}-{deviceId}
        Kind = ArchiveOpKinds.AssetAdded,
        Sha256 = Sha(n),
        PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
            $"aa/bb/{Sha(n)}.jpg", "image/jpeg", MediaKind.Image, 10, 10, 100,
            "pinterest", $"pin-{n}", null, null, $"Item {n}", null, null,
            null, DateTimeOffset.UnixEpoch.AddMinutes(n), null)),
    };

    private static string Sha(int n) => new(char.Parse((n % 10).ToString()), 64);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
