using Hoard.Core.Domain;
using Hoard.Core.Projects;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// P5/R1: the archive replicates to/from a dumb remote by file-set reconciliation — blobs before
/// segments, chapters converge by length, same-archive markers only, pull never deletes.
/// </summary>
public class ArchiveReplicatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-replicator-test", Guid.NewGuid().ToString("N"));

    public ArchiveReplicatorTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Push_seeds_an_empty_remote_with_the_whole_archive()
    {
        var project = SeedProject("A", blobs: 2, ops: 3);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-a"));

        var report = await ArchiveReplicator.PushAsync(project, remote);

        Assert.Equal(2, report.BlobsPushed);
        Assert.Equal(1, report.ChaptersPushed);
        Assert.True(File.Exists(Path.Combine(remote.Root, HoardProject.MarkerFileName)));
        Assert.Equal(2, (await remote.ListAsync("store/")).Count);
        Assert.Equal(3, ArchiveSegments.ReadAll(Path.Combine(remote.Root, "ops"), "dev-A").Count);

        // A second push moves nothing — the remote already holds everything.
        Assert.False((await ArchiveReplicator.PushAsync(project, remote)).AnythingMoved);
    }

    [Fact]
    public async Task Push_uploads_only_the_delta_and_never_reuploads_closed_chapters()
    {
        var project = SeedProject("B", blobs: 1, ops: 6, rotateBytes: 300); // several sealed chapters
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-b"));
        await ArchiveReplicator.PushAsync(project, remote);

        WriteBlob(project, "zz");
        ArchiveSegments.Append(project.OpsRoot, "dev-B", [Op("dev-B", 7)], rotateBytes: 300);

        var report = await ArchiveReplicator.PushAsync(project, remote);
        Assert.Equal(1, report.BlobsPushed);
        Assert.Equal(1, report.ChaptersPushed); // only the chapter op 7 landed in — sealed ones skipped
    }

    [Fact]
    public async Task Pull_onto_a_fresh_machine_reproduces_the_archive_and_its_index()
    {
        var source = SeedProject("C", blobs: 2, ops: 4);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-c"));
        await ArchiveReplicator.PushAsync(source, remote);

        // A fresh machine bootstraps a replica: same marker (the R2 "clone" flow), empty folder.
        var replicaRoot = Path.Combine(_dir, "C-replica");
        Directory.CreateDirectory(replicaRoot);
        File.Copy(source.MarkerPath, Path.Combine(replicaRoot, HoardProject.MarkerFileName));
        var replica = HoardProject.Open(replicaRoot);

        var report = await ArchiveReplicator.PullAsync(replica, remote);
        Assert.Equal(2, report.BlobsPulled);
        Assert.Equal(1, report.ChaptersPulled);

        // The pulled ops rebuild a working index — the whole point of a replica.
        var factory = new TestDbContextFactory(Path.Combine(_dir, "c-index.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(4, await ArchiveSync.CatchUpAsync(db, replica.OpsRoot, new ArchiveLog("fresh")));
            Assert.Equal(4, await db.Assets.CountAsync());
        }

        Assert.False((await ArchiveReplicator.PullAsync(replica, remote)).AnythingMoved); // converged
    }

    [Fact]
    public async Task Two_machines_converge_through_the_remote()
    {
        var machineA = SeedProject("D", blobs: 1, ops: 2);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-d"));
        await ArchiveReplicator.PushAsync(machineA, remote);

        // Machine B: same archive (cloned marker), its own device writing its own ops.
        var rootB = Path.Combine(_dir, "D-machine-b");
        Directory.CreateDirectory(rootB);
        File.Copy(machineA.MarkerPath, Path.Combine(rootB, HoardProject.MarkerFileName));
        var machineB = HoardProject.Open(rootB);
        await ArchiveReplicator.PullAsync(machineB, remote);
        ArchiveSegments.Append(machineB.OpsRoot, "dev-D-b", [Op("dev-D-b", 1)]);
        WriteBlob(machineB, "b1");
        await ArchiveReplicator.PushAsync(machineB, remote);

        await ArchiveReplicator.PullAsync(machineA, remote);
        Assert.Single(ArchiveSegments.ReadAll(machineA.OpsRoot, "dev-D-b")); // B's history arrived on A
        Assert.Contains("dev-D-b", ArchiveSegments.ListDevices(machineA.OpsRoot));
    }

    [Fact]
    public async Task Pull_never_replaces_this_devices_existing_chapter_but_still_bootstraps_an_absent_one()
    {
        var project = SeedProject("H", blobs: 0, ops: 2);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-h"));
        await ArchiveReplicator.PushAsync(project, remote);

        // The remote's copy of OUR chapter becomes longer but stale (a pushed torn tail, since repaired
        // locally). Length-max alone would clobber the authoritative local copy.
        var remoteChapter = Path.Combine(remote.Root, "ops", "dev-H.jsonl");
        File.AppendAllText(remoteChapter, new string('x', 500));
        var localChapter = ArchiveSegments.SegmentPath(project.OpsRoot, "dev-H");
        var localBytes = File.ReadAllBytes(localChapter);

        var report = await ArchiveReplicator.PullAsync(project, remote, localDeviceId: "dev-H");
        Assert.Equal(0, report.ChaptersPulled);
        Assert.Equal(localBytes, File.ReadAllBytes(localChapter)); // untouched — local writer is authoritative

        // But a WIPED local folder still bootstraps its own history back from the remote.
        File.Delete(localChapter);
        var restore = await ArchiveReplicator.PullAsync(project, remote, localDeviceId: "dev-H");
        Assert.Equal(1, restore.ChaptersPulled);
        Assert.Equal(2, ArchiveSegments.ReadAll(project.OpsRoot, "dev-H").Count); // torn tail tolerated by the reader
    }

    [Fact]
    public async Task Staging_temps_on_the_remote_are_invisible_to_listing_and_pull()
    {
        var project = SeedProject("I", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-i"));
        await ArchiveReplicator.PushAsync(project, remote);

        // A concurrent (or crash-orphaned) upload's staging file sits inside the remote tree.
        var temp = Path.Combine(remote.Root, "store", "zz", "xx", "blob.jpg.tmp-deadbeef");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(temp, "partial");

        Assert.DoesNotContain(await remote.ListAsync("store/"), o => o.RelativePath.Contains(".tmp-"));
        var report = await ArchiveReplicator.PullAsync(project, remote, localDeviceId: "dev-I");
        Assert.Equal(0, report.BlobsPulled); // the half-uploaded object was never a candidate
        Assert.False(File.Exists(Path.Combine(project.StoreRoot, "zz", "xx", "blob.jpg.tmp-deadbeef")));
    }

    [Fact]
    public async Task A_remote_holding_a_different_archive_is_refused()
    {
        var mine = SeedProject("E", blobs: 1, ops: 1);
        var theirs = SeedProject("F", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-e"));
        await ArchiveReplicator.PushAsync(theirs, remote);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PushAsync(mine, remote));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PullAsync(mine, remote));
    }

    [Fact]
    public async Task Pull_from_an_empty_remote_is_refused_and_pull_never_deletes_local_state()
    {
        var project = SeedProject("G", blobs: 2, ops: 2);
        var empty = new FileSystemRemoteStore(Path.Combine(_dir, "remote-empty"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PullAsync(project, empty));

        // A remote that lags behind (holds less than local) must never shrink the local archive.
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-g"));
        await ArchiveReplicator.PushAsync(project, remote);
        WriteBlob(project, "extra");
        ArchiveSegments.Append(project.OpsRoot, "dev-G", [Op("dev-G", 3)]);

        var report = await ArchiveReplicator.PullAsync(project, remote);
        Assert.False(report.AnythingMoved);
        Assert.Equal(3, Directory.EnumerateFiles(project.StoreRoot, "*", SearchOption.AllDirectories).Count());
        Assert.Equal(3, ArchiveSegments.ReadAll(project.OpsRoot, "dev-G").Count);
    }

    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>A real (born-v2) project folder with n store blobs and n ops in its device segment.</summary>
    private HoardProject SeedProject(string name, int blobs, int ops, long rotateBytes = ArchiveSegments.DefaultRotateBytes)
    {
        var project = HoardProject.Create(Path.Combine(_dir, name));
        for (var i = 0; i < blobs; i++) WriteBlob(project, $"{name}-{i}");
        ArchiveSegments.Append(project.OpsRoot, "dev-" + name,
            Enumerable.Range(1, ops).Select(n => Op("dev-" + name, n)), rotateBytes);
        return project;
    }

    private static void WriteBlob(HoardProject project, string seed)
    {
        var path = Path.Combine(project.StoreRoot, seed[..1], "xx", seed + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "blob-" + seed);
    }

    private static ArchiveOp Op(string deviceId, int n) => new()
    {
        DeviceId = deviceId,
        Seq = n,
        Hlc = $"{n:D14}-000000-{deviceId}",
        Kind = ArchiveOpKinds.AssetAdded,
        Sha256 = $"{deviceId}-{n}".PadRight(64, '0'),
        PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
            $"aa/bb/{deviceId}-{n}.jpg", "image/jpeg", MediaKind.Image, 10, 10, 100,
            "pinterest", $"pin-{n}", null, null, $"Item {n}", null, null,
            null, DateTimeOffset.UnixEpoch.AddMinutes(n), null)),
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
