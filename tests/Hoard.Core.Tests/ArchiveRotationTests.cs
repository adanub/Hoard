using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// P4 segment rotation: a device's op stream is cut into chapters at a size threshold — chapter zero is
/// the legacy <c>&lt;deviceId&gt;.jsonl</c>, continuations are <c>&lt;deviceId&gt;.00001.jsonl</c>, … —
/// and only the highest chapter is ever written, so a closed chapter's name AND content are final
/// (the property object-storage remotes and compaction rely on).
/// </summary>
public class ArchiveRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-rotation-test", Guid.NewGuid().ToString("N"));

    public ArchiveRotationTests() => Directory.CreateDirectory(_dir);

    // Small enough that a couple of ops fill a chapter; every op line here is ~200+ bytes.
    private const long TinyChapter = 300;

    [Fact]
    public void Append_rotates_into_numbered_chapters_and_reads_back_in_order()
    {
        var opsRoot = Path.Combine(_dir, "ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 8).Select(Op), TinyChapter);

        var chapters = ArchiveSegments.ListChapters(opsRoot, "dev");
        Assert.True(chapters.Count > 1, "expected the tiny threshold to produce multiple chapters");
        Assert.Equal(ArchiveSegments.SegmentPath(opsRoot, "dev"), chapters[0].Path); // legacy name = chapter 0
        Assert.Equal(Enumerable.Range(0, chapters.Count), chapters.Select(c => c.Chapter)); // contiguous

        Assert.Equal(Enumerable.Range(1, 8).Select(n => (long)n),
            ArchiveSegments.ReadAll(opsRoot, "dev").Select(o => o.Seq));
        Assert.Equal(8, ArchiveSegments.LastSeq(opsRoot, "dev"));
        Assert.Equal(new[] { "dev" }, ArchiveSegments.ListDevices(opsRoot)); // chapters group under one device
    }

    [Fact]
    public void Closed_chapters_are_never_touched_by_later_appends()
    {
        var opsRoot = Path.Combine(_dir, "closed-ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 6).Select(Op), TinyChapter);

        var closed = ArchiveSegments.ListChapters(opsRoot, "dev").SkipLast(1)
            .ToDictionary(c => c.Path, c => File.ReadAllBytes(c.Path));
        Assert.NotEmpty(closed);

        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(7, 6).Select(Op), TinyChapter);

        foreach (var (path, bytes) in closed)
            Assert.Equal(bytes, File.ReadAllBytes(path)); // byte-identical: closed = immutable
        Assert.Equal(12, ArchiveSegments.LastSeq(opsRoot, "dev"));
    }

    [Fact]
    public void Torn_tail_in_the_active_chapter_is_repaired_on_next_append()
    {
        var opsRoot = Path.Combine(_dir, "torn-ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 5).Select(Op), TinyChapter);

        var active = ArchiveSegments.ListChapters(opsRoot, "dev")[^1].Path;
        File.AppendAllText(active, "{\"seq\":99,\"torn"); // crash mid-append: no trailing newline

        ArchiveSegments.Append(opsRoot, "dev", [Op(6)], TinyChapter);
        Assert.Equal(Enumerable.Range(1, 6).Select(n => (long)n),
            ArchiveSegments.ReadAll(opsRoot, "dev").Select(o => o.Seq)); // garbage gone, stream intact
    }

    [Fact]
    public async Task Catch_up_replays_a_rotated_chain_into_a_fresh_index()
    {
        var factory = new TestDbContextFactory(Path.Combine(_dir, "chain.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var opsRoot = Path.Combine(_dir, "chain-ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 10).Select(Op), TinyChapter);

        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(10, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host")));
            Assert.Equal(10, await db.Assets.CountAsync());
            Assert.Equal(0, await ArchiveSync.CatchUpAsync(db, opsRoot, new ArchiveLog("host")));
        }
    }

    [Fact]
    public async Task Flush_watermark_spans_the_whole_chain()
    {
        // The table holds ops 1..8; the chain already carries 1..6 (rotated). A flush must derive its
        // watermark across ALL chapters — reading only one file would re-append history it already holds.
        var factory = new TestDbContextFactory(Path.Combine(_dir, "flush.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        var opsRoot = Path.Combine(_dir, "flush-ops");
        ArchiveSegments.Append(opsRoot, "dev", Enumerable.Range(1, 6).Select(Op), TinyChapter);

        var archive = new ArchiveLog("dev", opsRoot: () => opsRoot);
        await using (var db = factory.CreateDbContext())
        {
            db.ArchiveOps.AddRange(Enumerable.Range(1, 8).Select(Op));
            await db.SaveChangesAsync();
            await archive.FlushSegmentAsync(db);
        }

        Assert.Equal(Enumerable.Range(1, 8).Select(n => (long)n),
            ArchiveSegments.ReadAll(opsRoot, "dev").Select(o => o.Seq)); // only 7 and 8 were appended
    }

    private static ArchiveOp Op(int n) => new()
    {
        DeviceId = "dev",
        Seq = n,
        Hlc = $"{n:D14}-000000-dev",
        Kind = ArchiveOpKinds.AssetAdded,
        Sha256 = new string(char.Parse((n % 10).ToString()), 64),
        PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
            $"aa/bb/{n}.jpg", "image/jpeg", MediaKind.Image, 10, 10, 100,
            "pinterest", $"pin-{n}", null, null, $"Item {n}", null, null,
            null, DateTimeOffset.UnixEpoch.AddMinutes(n), null)),
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
