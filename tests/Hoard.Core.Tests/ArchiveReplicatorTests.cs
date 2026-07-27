using Hoard.Core.Domain;
using Hoard.Core.Projects;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// P5/R1: the archive replicates to/from a dumb remote by file-set reconciliation — blobs before
/// segments, chapters converge by length, same-archive markers only, pull never deletes. Delta mode adds
/// the rule the whole feature stands on: a chapter is the RECEIPT, so it is only ever published once the
/// blobs its new ops name have landed.
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

        var report = await ArchiveReplicator.PushAsync(project, remote, "dev-A");

        Assert.Equal(2, report.BlobsPushed);
        Assert.Equal(1, report.ChaptersPushed);
        Assert.True(File.Exists(Path.Combine(remote.Root, HoardProject.MarkerFileName)));
        Assert.Equal(2, (await remote.ListAsync("store/")).Count);
        Assert.Equal(3, ArchiveSegments.ReadAll(Path.Combine(remote.Root, "ops"), "dev-A").Count);

        // A second push moves nothing — the remote already holds everything.
        Assert.False((await ArchiveReplicator.PushAsync(project, remote, "dev-A")).AnythingMoved);
    }

    [Fact]
    public async Task Push_uploads_only_the_delta_and_never_reuploads_closed_chapters()
    {
        var project = SeedProject("B", blobs: 6, ops: 6, rotateBytes: 300); // several sealed chapters
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-b"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-B");

        AddChange(project, "dev-B", 7, rotateBytes: 300);

        var report = await ArchiveReplicator.PushAsync(project, remote, "dev-B");
        Assert.Equal(1, report.BlobsPushed);
        Assert.Equal(1, report.ChaptersPushed); // only the chapter op 7 landed in — sealed ones skipped
    }

    [Fact]
    public async Task An_idle_sync_never_lists_the_remote_store()
    {
        // The reason delta mode exists: a backup of a big archive with nothing new must cost what
        // CHANGED, not what the archive holds. Re-listing store/ is the 10-minutes-over-SMB bug.
        var project = SeedProject("Idle", blobs: 30, ops: 30);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-idle")));
        await ArchiveReplicator.PushAsync(project, remote, "dev-Idle");

        remote.Reset();
        var report = await ArchiveReplicator.PushAsync(project, remote, "dev-Idle");

        Assert.False(report.AnythingMoved);
        Assert.DoesNotContain(remote.ListedPrefixes, p => p.StartsWith("store", StringComparison.Ordinal));
        Assert.Empty(remote.Uploads);
        Assert.InRange(remote.TotalCalls, 1, 6); // the marker + a stat per chapter, regardless of size
    }

    [Fact]
    public async Task A_torn_remote_chapter_is_taken_once_and_then_converges()
    {
        // Measuring the local side by its whole-line prefix while the remote reports raw bytes never
        // settles: taking the chapter makes the files identical, yet the comparison still says "behind",
        // so every sync re-fetches and re-applies it and the UI never says "already in sync".
        var source = SeedProject("Converge", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-converge"));
        await ArchiveReplicator.PushAsync(source, remote, "dev-Converge");
        // A torn tail, as an older build could publish: raw length runs past the last complete line.
        File.AppendAllText(Path.Combine(remote.Root, "ops", "dev-Converge.jsonl"), "{\"seq\":9,\"hl");

        var replica = Clone(source, "Converge-replica");
        Assert.Equal(1, (await ArchiveReplicator.PullAsync(replica, remote, "fresh")).ChaptersPulled);
        Assert.Equal(0, (await ArchiveReplicator.PullAsync(replica, remote, "fresh")).ChaptersPulled);
        Assert.Single(ArchiveSegments.ReadAll(replica.OpsRoot, "dev-Converge")); // the torn line is not an op
    }

    [Fact]
    public async Task A_blob_that_fails_to_download_holds_back_every_chapter_that_names_it()
    {
        // "Handled" has to mean SETTLED. Marking a blob done the moment it's first seen lets a second
        // chapter naming it sail through after the first deferred — publishing ops whose image never
        // arrived, at a length no future delta pull looks below.
        var source = SeedProject("Share", blobs: 0, ops: 0);
        WriteBlobAt(source, "aa/bb/shared.jpg", "shared-bytes");
        ArchiveSegments.Append(source.OpsRoot, "dev-one", [OpNaming("dev-one", 1, "aa/bb/shared.jpg")]);
        ArchiveSegments.Append(source.OpsRoot, "dev-two", [OpNaming("dev-two", 1, "aa/bb/shared.jpg")]);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-share")));
        await ArchiveReplicator.PushAsync(source, remote, "dev-one");

        var replica = Clone(source, "Share-replica");
        remote.FailDownload = p => p.EndsWith("shared.jpg", StringComparison.Ordinal);
        var blocked = await ArchiveReplicator.PullAsync(replica, remote, "fresh");

        Assert.Equal(0, blocked.ChaptersPulled);   // BOTH chapters wait, not just the first
        Assert.Equal(2, blocked.ChaptersDeferred);
        Assert.Empty(Directory.EnumerateFiles(replica.OpsRoot, "*.jsonl"));

        remote.FailDownload = null;
        var retried = await ArchiveReplicator.PullAsync(replica, remote, "fresh");
        Assert.Equal(2, retried.ChaptersPulled);
        Assert.Equal(1, retried.BlobsPulled);
    }

    [Fact]
    public async Task A_chapter_is_not_published_when_one_of_its_blobs_fails_to_upload()
    {
        // Publishing a chapter tells every future run "everything below this offset is backed up". If a
        // blob it names never made it, that claim is a permanent, silent hole in the backup.
        var project = SeedProject("Fail", blobs: 2, ops: 2);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-fail")))
        {
            FailUpload = path => path.EndsWith("dev-Fail-2.jpg", StringComparison.Ordinal),
        };

        var blocked = await ArchiveReplicator.PushAsync(project, remote, "dev-Fail");
        Assert.Equal(1, blocked.BlobsPushed);
        Assert.Equal(0, blocked.ChaptersPushed);
        Assert.Equal(1, blocked.ChaptersDeferred);
        Assert.Null(await remote.GetLengthAsync("ops/dev-Fail.jsonl"));

        // The next run recomputes the same delta and completes it — nothing was lost, only delayed.
        remote.FailUpload = null;
        var retried = await ArchiveReplicator.PushAsync(project, remote, "dev-Fail");
        Assert.Equal(1, retried.BlobsPushed);
        Assert.Equal(1, retried.ChaptersPushed);
        Assert.Equal(2, (await remote.ListAsync("store/")).Count);
    }

    [Fact]
    public async Task A_chapter_that_grows_mid_push_never_publishes_the_ops_that_arrived_late()
    {
        // An import (or another machine on a shared folder) can append while we are busy uploading blobs.
        // Uploading the file as it is THEN would carry ops whose blobs were never scanned — and the
        // remote's new length would mark them backed up forever. Push publishes the bytes it scanned.
        var project = SeedProject("Race", blobs: 1, ops: 1);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-race")))
        {
            BeforeFirstUpload = () => AddChange(project, "dev-Race", 2),
        };

        var first = await ArchiveReplicator.PushAsync(project, remote, "dev-Race");
        Assert.Equal(1, first.BlobsPushed);
        Assert.Equal(1, first.ChaptersPushed);
        Assert.Single(ArchiveSegments.ReadAll(Path.Combine(remote.Root, "ops"), "dev-Race")); // only op 1

        // The late op is simply the next run's delta, blob and all.
        remote.BeforeFirstUpload = null;
        var second = await ArchiveReplicator.PushAsync(project, remote, "dev-Race");
        Assert.Equal(1, second.BlobsPushed);
        Assert.Equal(2, ArchiveSegments.ReadAll(Path.Combine(remote.Root, "ops"), "dev-Race").Count);
        Assert.Equal(2, (await remote.ListAsync("store/")).Count);
    }

    [Fact]
    public async Task A_re_emitted_added_op_for_an_unchanged_blob_does_not_re_upload_it()
    {
        // A re-crawl re-emits asset.added for a metadata-only change (a title edit, a board move). Those
        // ops are new, but their blob is not — re-uploading thousands of unchanged images would be worse
        // than the full listing this replaces.
        var project = SeedProject("Same", blobs: 1, ops: 1);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-same")));
        await ArchiveReplicator.PushAsync(project, remote, "dev-Same");

        ArchiveSegments.Append(project.OpsRoot, "dev-Same", [Op("dev-Same", 1, seq: 2)]);
        remote.Reset();
        var report = await ArchiveReplicator.PushAsync(project, remote, "dev-Same");

        Assert.Equal(0, report.BlobsPushed);
        Assert.Equal(1, report.ChaptersPushed);
        Assert.DoesNotContain(remote.Uploads, p => p.StartsWith("store/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_payload_path_that_escapes_the_store_is_refused()
    {
        // Op payloads are archive content — a corrupt or hostile one must never steer a write outside
        // the store, in either direction.
        var project = SeedProject("Escape", blobs: 0, ops: 0);
        ArchiveSegments.Append(project.OpsRoot, "dev-Escape", [OpNaming("dev-Escape", 1, "../../evil.txt")]);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-escape")));

        var push = await ArchiveReplicator.PushAsync(project, remote, "dev-Escape");
        Assert.Equal(0, push.BlobsPushed);
        Assert.DoesNotContain(remote.Uploads, p => p.Contains("evil", StringComparison.Ordinal));

        var replica = Clone(project, "Escape-replica");
        var pull = await ArchiveReplicator.PullAsync(replica, remote, "dev-other");
        Assert.Equal(1, pull.ChaptersPulled);
        Assert.Equal(0, pull.BlobsPulled);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public async Task A_repaired_own_chapter_shorter_than_a_stale_longer_remote_copy_still_pushes()
    {
        // An older build could push a torn tail. Once the local writer repairs past it the local file is
        // SHORTER — "the longer copy wins" would then wedge that chapter, and under delta its blobs with
        // it. Our own chapter is authoritative: any difference pushes.
        var project = SeedProject("Torn", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-torn"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-Torn");
        File.AppendAllText(Path.Combine(remote.Root, "ops", "dev-Torn.jsonl"), new string('x', 500));

        AddChange(project, "dev-Torn", 2);
        var report = await ArchiveReplicator.PushAsync(project, remote, "dev-Torn");

        Assert.Equal(1, report.ChaptersPushed);
        Assert.Equal(1, report.BlobsPushed);
        Assert.Equal(2, ArchiveSegments.ReadAll(Path.Combine(remote.Root, "ops"), "dev-Torn").Count);
    }

    [Fact]
    public async Task Full_mode_repairs_a_blob_deleted_from_the_remote_behind_our_back()
    {
        // The honest limit of delta mode, and the reason Repair backup exists: the op log can't describe
        // damage done to the remote from outside it.
        var project = SeedProject("Repair", blobs: 2, ops: 2);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-repair"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-Repair");
        File.Delete(Path.Combine(remote.Root, "store", "aa", "bb", "dev-Repair-1.jpg"));

        Assert.False((await ArchiveReplicator.PushAsync(project, remote, "dev-Repair")).AnythingMoved);

        var repaired = await ArchiveReplicator.PushAsync(project, remote, "dev-Repair", ReplicationMode.Full);
        Assert.Equal(1, repaired.BlobsPushed);
        Assert.True(repaired.Verified);
        Assert.Equal(2, (await remote.ListAsync("store/")).Count);
    }

    [Fact]
    public async Task Pull_onto_a_fresh_machine_reproduces_the_archive_and_its_index()
    {
        var source = SeedProject("C", blobs: 4, ops: 4);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-c"));
        await ArchiveReplicator.PushAsync(source, remote, "dev-C");

        // A fresh machine bootstraps a replica: same marker (the R2 "clone" flow), empty folder.
        var replica = Clone(source, "C-replica");

        var report = await ArchiveReplicator.PullAsync(replica, remote, "fresh");
        Assert.Equal(4, report.BlobsPulled);
        Assert.Equal(1, report.ChaptersPulled);

        // The pulled ops rebuild a working index — the whole point of a replica.
        var factory = new TestDbContextFactory(Path.Combine(_dir, "c-index.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(4, await ArchiveSync.CatchUpAsync(db, replica.OpsRoot, new ArchiveLog("fresh")));
            Assert.Equal(4, await db.Assets.CountAsync());
        }

        Assert.False((await ArchiveReplicator.PullAsync(replica, remote, "fresh")).AnythingMoved); // converged
    }

    [Fact]
    public async Task Delta_pull_takes_only_the_blobs_its_new_ops_name()
    {
        var source = SeedProject("Inc", blobs: 2, ops: 2);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-inc")));
        await ArchiveReplicator.PushAsync(source, remote, "dev-Inc");

        var replica = Clone(source, "Inc-replica");
        await ArchiveReplicator.PullAsync(replica, remote, "fresh");

        AddChange(source, "dev-Inc", 3);
        await ArchiveReplicator.PushAsync(source, remote, "dev-Inc");

        remote.Reset();
        var report = await ArchiveReplicator.PullAsync(replica, remote, "fresh");

        Assert.Equal(1, report.BlobsPulled);
        Assert.Equal(1, report.ChaptersPulled);
        Assert.DoesNotContain(remote.ListedPrefixes, p => p.StartsWith("store", StringComparison.Ordinal));
        Assert.Single(remote.Downloads, p => p.StartsWith("store/", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(replica.OpsRoot, "*.tmp-*")); // staging never survives a run
    }

    [Fact]
    public async Task Two_machines_converge_through_the_remote()
    {
        var machineA = SeedProject("D", blobs: 2, ops: 2);
        var remote = new RecordingRemoteStore(new FileSystemRemoteStore(Path.Combine(_dir, "remote-d")));
        await ArchiveReplicator.PushAsync(machineA, remote, "dev-D");

        // Machine B: same archive (cloned marker), its own device writing its own ops.
        var machineB = Clone(machineA, "D-machine-b");
        await ArchiveReplicator.PullAsync(machineB, remote, "dev-D-b");
        AddChange(machineB, "dev-D-b", 1);

        remote.Reset();
        await ArchiveReplicator.PushAsync(machineB, remote, "dev-D-b");
        // B relays A's chapter without re-sending a byte of A's images: the remote already has that
        // chapter at the same length, so none of its ops are ever re-examined.
        Assert.DoesNotContain(remote.Uploads, p => p.Contains("dev-D-", StringComparison.Ordinal) && p.Contains("store/", StringComparison.Ordinal) && !p.Contains("dev-D-b", StringComparison.Ordinal));

        await ArchiveReplicator.PullAsync(machineA, remote, "dev-D");
        Assert.Single(ArchiveSegments.ReadAll(machineA.OpsRoot, "dev-D-b")); // B's history arrived on A
        Assert.Contains("dev-D-b", ArchiveSegments.ListDevices(machineA.OpsRoot));
    }

    [Fact]
    public async Task Pull_never_replaces_this_devices_existing_chapter_but_still_bootstraps_an_absent_one()
    {
        var project = SeedProject("H", blobs: 0, ops: 2);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-h"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-H");

        // The remote's copy of OUR chapter becomes longer but stale (a pushed torn tail, since repaired
        // locally). Length-max alone would clobber the authoritative local copy.
        var remoteChapter = Path.Combine(remote.Root, "ops", "dev-H.jsonl");
        File.AppendAllText(remoteChapter, new string('x', 500));
        var localChapter = ArchiveSegments.SegmentPath(project.OpsRoot, "dev-H");
        var localBytes = File.ReadAllBytes(localChapter);

        var report = await ArchiveReplicator.PullAsync(project, remote, "dev-H");
        Assert.Equal(0, report.ChaptersPulled);
        Assert.Equal(localBytes, File.ReadAllBytes(localChapter)); // untouched — local writer is authoritative

        // But a WIPED local folder still bootstraps its own history back from the remote.
        File.Delete(localChapter);
        var restore = await ArchiveReplicator.PullAsync(project, remote, "dev-H");
        Assert.Equal(1, restore.ChaptersPulled);
        Assert.Equal(2, ArchiveSegments.ReadAll(project.OpsRoot, "dev-H").Count); // torn tail tolerated by the reader
    }

    [Fact]
    public async Task Staging_temps_on_the_remote_are_invisible_to_listing_and_pull()
    {
        var project = SeedProject("I", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-i"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-I");

        // A concurrent (or crash-orphaned) upload's staging file sits inside the remote tree.
        var temp = Path.Combine(remote.Root, "store", "zz", "xx", "blob.jpg.tmp-deadbeef");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(temp, "partial");

        Assert.DoesNotContain(await remote.ListAsync("store/"), o => o.RelativePath.Contains(".tmp-"));
        // Full mode is the leg that reads the store listing, so it's the one that could take the stray.
        var report = await ArchiveReplicator.PullAsync(project, remote, "dev-I", ReplicationMode.Full);
        Assert.Equal(0, report.BlobsPulled); // the half-uploaded object was never a candidate
        Assert.False(File.Exists(Path.Combine(project.StoreRoot, "zz", "xx", "blob.jpg.tmp-deadbeef")));
    }

    [Fact]
    public async Task A_remote_holding_a_different_archive_is_refused()
    {
        var mine = SeedProject("E", blobs: 1, ops: 1);
        var theirs = SeedProject("F", blobs: 1, ops: 1);
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-e"));
        await ArchiveReplicator.PushAsync(theirs, remote, "dev-F");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PushAsync(mine, remote, "dev-E"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PullAsync(mine, remote, "dev-E"));
    }

    [Fact]
    public async Task Pull_from_an_empty_remote_is_refused_and_pull_never_deletes_local_state()
    {
        var project = SeedProject("G", blobs: 2, ops: 2);
        var empty = new FileSystemRemoteStore(Path.Combine(_dir, "remote-empty"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveReplicator.PullAsync(project, empty, "dev-G"));

        // A remote that lags behind (holds less than local) must never shrink the local archive.
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote-g"));
        await ArchiveReplicator.PushAsync(project, remote, "dev-G");
        AddChange(project, "dev-G", 3);

        var report = await ArchiveReplicator.PullAsync(project, remote, "dev-G");
        Assert.False(report.AnythingMoved);
        Assert.Equal(3, Directory.EnumerateFiles(project.StoreRoot, "*", SearchOption.AllDirectories).Count());
        Assert.Equal(3, ArchiveSegments.ReadAll(project.OpsRoot, "dev-G").Count);
    }

    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>A real (born-v2) project folder whose ops NAME its blobs — op n points at
    /// <c>store/aa/bb/&lt;device&gt;-n.jpg</c>, and the first <paramref name="blobs"/> of them exist on
    /// disk. Delta replication moves what the ops reference, so the two must agree to mean anything.</summary>
    private HoardProject SeedProject(string name, int blobs, int ops, long rotateBytes = ArchiveSegments.DefaultRotateBytes)
    {
        var project = HoardProject.Create(Path.Combine(_dir, name));
        var deviceId = "dev-" + name;
        for (var i = 1; i <= blobs; i++) WriteBlob(project, deviceId, i);
        if (ops > 0)
            ArchiveSegments.Append(project.OpsRoot, deviceId,
                Enumerable.Range(1, ops).Select(n => Op(deviceId, n)), rotateBytes);
        return project;
    }

    /// <summary>One more change on a device's stream: the blob lands first, then the op that names it —
    /// the archive's own ordering.</summary>
    private static void AddChange(HoardProject project, string deviceId, int n,
        long rotateBytes = ArchiveSegments.DefaultRotateBytes)
    {
        WriteBlob(project, deviceId, n);
        ArchiveSegments.Append(project.OpsRoot, deviceId, [Op(deviceId, n)], rotateBytes);
    }

    /// <summary>A second machine on the SAME archive: the marker copied into an empty folder.</summary>
    private HoardProject Clone(HoardProject source, string name)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        File.Copy(source.MarkerPath, Path.Combine(root, HoardProject.MarkerFileName));
        return HoardProject.Open(root);
    }

    private static string BlobRelative(string deviceId, int n) => $"aa/bb/{deviceId}-{n}.jpg";
    private static string BlobContent(string deviceId, int n) => $"blob-{deviceId}-{n}";

    private static void WriteBlob(HoardProject project, string deviceId, int n)
        => WriteBlobAt(project, BlobRelative(deviceId, n), BlobContent(deviceId, n));

    private static void WriteBlobAt(HoardProject project, string relativePath, string content)
    {
        var path = Path.Combine(project.StoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static ArchiveOp Op(string deviceId, int n, int? seq = null)
        => OpNaming(deviceId, n, BlobRelative(deviceId, n), seq);

    private static ArchiveOp OpNaming(string deviceId, int n, string relativePath, int? seq = null) => new()
    {
        DeviceId = deviceId,
        Seq = seq ?? n,
        Hlc = $"{seq ?? n:D14}-000000-{deviceId}",
        Kind = ArchiveOpKinds.AssetAdded,
        Sha256 = $"{deviceId}-{n}".PadRight(64, '0'),
        PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
            relativePath, "image/jpeg", MediaKind.Image, 10, 10, BlobContent(deviceId, n).Length,
            "pinterest", $"pin-{n}", null, null, $"Item {n}", null, null,
            null, DateTimeOffset.UnixEpoch.AddMinutes(n), null)),
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
