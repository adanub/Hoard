using Hoard.Core.Domain;
using Hoard.Core.Projects;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// P5/R2: the composite sync (pull → apply to this machine's index → push) and the per-machine remote
/// config it runs from.
/// </summary>
public class RemoteSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-remotesync-test", Guid.NewGuid().ToString("N"));

    public RemoteSyncTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Remote_config_round_trips_and_a_garbled_file_reads_as_none()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var projectId = Guid.NewGuid();
        Assert.Null(RemoteConfig.Load(appPaths, projectId));

        new RemoteConfig(RemoteConfig.FolderType, "/backup/hoard").Save(appPaths, projectId);
        var loaded = RemoteConfig.Load(appPaths, projectId);
        Assert.Equal("/backup/hoard", loaded!.Target);
        Assert.IsType<FileSystemRemoteStore>(loaded.CreateStore());

        File.WriteAllText(appPaths.ProjectRemoteConfigPath(projectId), "{not json");
        Assert.Null(RemoteConfig.Load(appPaths, projectId));

        RemoteConfig.Remove(appPaths, projectId);
        Assert.False(File.Exists(appPaths.ProjectRemoteConfigPath(projectId)));
    }

    [Fact]
    public async Task Sync_seeds_an_empty_remote_then_carries_changes_to_a_second_machine_and_back()
    {
        var remote = new FileSystemRemoteStore(Path.Combine(_dir, "remote"));

        // Machine A: a project with one imported asset (op + blob), index built.
        var (projectA, factoryA, archiveA) = Machine("A");
        SeedAsset(projectA, "dev-A", 1);
        await CatchUp(factoryA, projectA, archiveA);

        // First sync: nothing to pull (empty remote), everything pushed.
        var first = await RemoteSync.SyncAsync(projectA, remote, factoryA, archiveA);
        Assert.Equal(1, first.BlobsPushed);
        Assert.Equal(0, first.BlobsPulled);

        // Machine B: a clone (same marker), fresh index. Sync pulls A's history AND applies it — the
        // asset is queryable immediately, no re-open needed.
        var (projectB, factoryB, archiveB) = Machine("B", cloneMarkerFrom: projectA);
        var join = await RemoteSync.SyncAsync(projectB, remote, factoryB, archiveB);
        Assert.Equal(1, join.BlobsPulled);
        await using (var db = factoryB.CreateDbContext())
            Assert.Equal(1, await db.Assets.CountAsync());

        // B adds its own asset; sync; A syncs and sees it applied to A's index.
        SeedAsset(projectB, "dev-B", 1);
        await CatchUp(factoryB, projectB, archiveB);
        await RemoteSync.SyncAsync(projectB, remote, factoryB, archiveB);

        var back = await RemoteSync.SyncAsync(projectA, remote, factoryA, archiveA);
        Assert.Equal(1, back.BlobsPulled);
        await using (var db = factoryA.CreateDbContext())
            Assert.Equal(2, await db.Assets.CountAsync());

        // Everyone converged: another sync anywhere moves nothing.
        Assert.False((await RemoteSync.SyncAsync(projectA, remote, factoryA, archiveA)).AnythingMoved);
        Assert.False((await RemoteSync.SyncAsync(projectB, remote, factoryB, archiveB)).AnythingMoved);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private (HoardProject Project, TestDbContextFactory Factory, ArchiveLog Archive) Machine(
        string name, HoardProject? cloneMarkerFrom = null)
    {
        var root = Path.Combine(_dir, "machine-" + name);
        HoardProject project;
        if (cloneMarkerFrom is null)
        {
            project = HoardProject.Create(root);
        }
        else
        {
            Directory.CreateDirectory(root);
            File.Copy(cloneMarkerFrom.MarkerPath, Path.Combine(root, HoardProject.MarkerFileName));
            project = HoardProject.Open(root);
        }
        var factory = new TestDbContextFactory(Path.Combine(_dir, $"index-{name}.db"));
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        return (project, factory, new ArchiveLog("dev-" + name, opsRoot: () => project.OpsRoot));
    }

    /// <summary>One imported asset, at the file level: its op in the device's segment, its blob in store/.</summary>
    private static void SeedAsset(HoardProject project, string deviceId, int n)
    {
        var sha = $"{deviceId}-{n}".PadRight(64, '0');
        var relative = $"aa/bb/{sha}.jpg";
        var blob = Path.Combine(project.StoreRoot, "aa", "bb", sha + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllText(blob, "blob-" + sha);
        ArchiveSegments.Append(project.OpsRoot, deviceId, [new ArchiveOp
        {
            DeviceId = deviceId,
            Seq = n,
            Hlc = $"{n:D14}-000000-{deviceId}",
            Kind = ArchiveOpKinds.AssetAdded,
            Sha256 = sha,
            PayloadJson = ArchiveOpJson.Serialize(new AssetAddedPayload(
                relative, "image/jpeg", Hoard.Core.Domain.MediaKind.Image, 10, 10, 100,
                "pinterest", $"pin-{deviceId}-{n}", null, null, $"Item {n}", null, null,
                null, DateTimeOffset.UnixEpoch.AddMinutes(n), null)),
        }]);
    }

    private static async Task CatchUp(TestDbContextFactory factory, HoardProject project, ArchiveLog archive)
    {
        await using var db = factory.CreateDbContext();
        await ArchiveSync.SyncAtOpenAsync(db, project.OpsRoot, archive);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
