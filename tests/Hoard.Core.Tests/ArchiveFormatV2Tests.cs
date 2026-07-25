using System.Text.Json;
using Hoard.Core.Connectors;
using Hoard.Core.Domain;
using Hoard.Core.Ingest;
using Hoard.Core.Library;
using Hoard.Core.Projects;
using Hoard.Core.Storage;
using Hoard.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// The SYNC-DESIGN P3 proof: format v2 puts only static content in the project folder and derives each
/// machine's index under app data — born-v2 projects, the one-way v1 migration, a second machine building
/// its index from the archive alone, and a deleted index rebuilding (including this device's own ops)
/// with no sequence collisions.
/// </summary>
public class ArchiveFormatV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-v2-test", Guid.NewGuid().ToString("N"));

    public ArchiveFormatV2Tests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task New_projects_are_born_v2_with_the_index_under_app_data()
    {
        var machine = Machine("appdata-1", "device-1");
        var folder = Path.Combine(_dir, "Fresh Project");

        var project = machine.Projects.Create(folder);
        await machine.Factory.EnsureCreatedAsync();
        await Import(machine, project);

        Assert.Equal(HoardProject.CurrentFormatVersion, HoardProject.Peek(folder).FormatVersion);
        Assert.False(File.Exists(Path.Combine(folder, "hoard.db"))); // nothing live in the archive folder
        Assert.True(File.Exists(machine.Projects.AppPaths.IndexDbPath(project.Id)));
        Assert.True(File.Exists(ArchiveSegments.SegmentPath(project.OpsRoot, "device-1")));

        var stats = Assert.IsType<ProjectStats>(await ProjectStatsReader.ReadAsync(folder, machine.Projects.AppPaths));
        Assert.Equal(3, stats.Images); // the stats reader resolves the app-data index
    }

    [Fact]
    public async Task Open_sweeps_derived_data_an_older_build_left_in_a_v2_folder()
    {
        var machine = Machine("appdata-1", "device-1");
        var folder = Path.Combine(_dir, "Sweep Me");
        var project = machine.Projects.Create(folder);
        await machine.Factory.EnsureCreatedAsync();

        // What a pre-P4 build kept in the archive folder: thumbnails, a skip-archive, import transcripts.
        Directory.CreateDirectory(project.ThumbnailsRoot);
        File.WriteAllText(Path.Combine(project.ThumbnailsRoot, "abc_256.png"), "png");
        File.WriteAllText(project.DownloadArchivePath, "archive");
        Directory.CreateDirectory(project.LogsRoot);
        File.WriteAllText(Path.Combine(project.LogsRoot, "import-1.log"), "transcript");

        // FRESH caches are spared: a sibling machine on a pre-P4 build could still be writing them
        // (deleting its download archive mid-import would lose its progress).
        await machine.Factory.EnsureCreatedAsync();
        Assert.True(Directory.Exists(project.ThumbnailsRoot));
        Assert.True(File.Exists(project.DownloadArchivePath));

        // Once quiescent (nothing has touched them for long enough), the next open sweeps them.
        var old = DateTime.UtcNow.AddHours(-2);
        Directory.SetLastWriteTimeUtc(project.ThumbnailsRoot, old);
        File.SetLastWriteTimeUtc(project.DownloadArchivePath, old);
        await machine.Factory.EnsureCreatedAsync();

        Assert.False(Directory.Exists(project.ThumbnailsRoot));            // regenerable — deleted
        Assert.False(File.Exists(project.DownloadArchivePath));            // rebuilt per import — deleted
        Assert.False(Directory.Exists(project.LogsRoot));                  // transcripts moved…
        Assert.True(File.Exists(Path.Combine(                              // …to this machine's app data
            machine.Projects.AppPaths.ProjectLogsRoot(project.Id), "import-1.log")));
    }

    [Fact]
    public async Task Legacy_project_migrates_one_way_with_backup_and_equivalent_index()
    {
        var folder = SeedLegacyProject("Legacy Project");
        var machine = Machine("appdata-1", "device-1");

        var project = machine.Projects.Open(folder);
        Assert.Equal(1, project.FormatVersion);

        await machine.Factory.EnsureCreatedAsync(upgradeLegacyFormat: true);

        // The folder is now static-only: marker stamped v2, DB replaced by the rollback backup + segments.
        Assert.Equal(HoardProject.CurrentFormatVersion, HoardProject.Peek(folder).FormatVersion);
        Assert.False(File.Exists(Path.Combine(folder, "hoard.db")));
        Assert.True(File.Exists(Path.Combine(folder, "hoard.db" + ArchiveMigration.BackupSuffix)));
        Assert.True(File.Exists(ArchiveSegments.SegmentPath(project.OpsRoot, "device-1")));

        // The machine's index equals the backup exactly (the pre-migration data can't be compared
        // verbatim — migration MINTS the cross-device uids), and the seeded content is all there.
        var backup = await ArchiveTestProjection.ProjectAsync(
            new TestDbContextFactory(Path.Combine(folder, "hoard.db" + ArchiveMigration.BackupSuffix)));
        var indexPath = machine.Projects.AppPaths.IndexDbPath(project.Id);
        var index = await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(indexPath));
        Assert.Equal(backup, index);
        Assert.Contains("legacy-sha-1", index);
        Assert.Contains("legacy-sha-2", index);
        Assert.Contains("collection|", index);

        // Reopening is a plain v2 open — no re-migration, still fully usable.
        machine.Projects.Open(folder);
        await machine.Factory.EnsureCreatedAsync();
        Assert.Equal(backup, await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(indexPath)));
    }

    [Fact]
    public async Task A_second_machine_builds_its_index_from_the_archive_alone()
    {
        // Machine 1 migrates a legacy project (its history becomes the op segment)…
        var folder = SeedLegacyProject("Shared Project");
        var machine1 = Machine("appdata-1", "device-1");
        var project1 = machine1.Projects.Open(folder);
        await machine1.Factory.EnsureCreatedAsync(upgradeLegacyFormat: true);

        // …and machine 2 (fresh app data, no history) opens the same folder: its index is derived
        // entirely from the marker + segments. This is the NAS scenario end-to-end.
        var machine2 = Machine("appdata-2", "device-2");
        var project2 = machine2.Projects.Open(folder);
        await machine2.Factory.EnsureCreatedAsync();

        Assert.Equal(project1.Id, project2.Id); // same archive identity via the marker
        Assert.Equal(
            await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(machine1.Projects.AppPaths.IndexDbPath(project1.Id))),
            await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(machine2.Projects.AppPaths.IndexDbPath(project2.Id))));

        // Machine 2 curates; machine 1 sees it after its next open-time catch-up.
        int boardId;
        await using (var db = machine2.Factory.CreateDbContext())
            boardId = (await db.Collections.SingleAsync(c => c.Name == "Alpha")).Id;
        await machine2.Curation.RenameBoardAsync(boardId, "Alpha (from machine 2)");

        machine1.Projects.Open(folder);
        await machine1.Factory.EnsureCreatedAsync();
        var machine1View = await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(machine1.Projects.AppPaths.IndexDbPath(project1.Id)));
        Assert.Contains("Alpha (from machine 2)", machine1View);
    }

    [Fact]
    public async Task A_deleted_index_rebuilds_from_segments_including_own_ops_without_seq_collision()
    {
        var machine = Machine("appdata-1", "device-1");
        var folder = Path.Combine(_dir, "Rebuild Me");
        var project = machine.Projects.Create(folder);
        await machine.Factory.EnsureCreatedAsync();
        await Import(machine, project);

        var indexPath = machine.Projects.AppPaths.IndexDbPath(project.Id);
        var before = await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(indexPath));

        // Wipe this machine's derived state and simulate an app restart (a fresh ArchiveLog, so the seq
        // counter must re-derive from what catch-up replays, not from memory).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(machine.Projects.AppPaths.ProjectStateRoot(project.Id), recursive: true);
        var restarted = Machine("appdata-1", "device-1");

        restarted.Projects.Open(folder);
        await restarted.Factory.EnsureCreatedAsync();
        Assert.Equal(before, await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(indexPath)));

        // The next local write must mint a FRESH seq beyond the replayed history (the unique
        // (DeviceId, Seq) index throws if the counter restarted) — this is ObserveOwnSeq's guarantee.
        int boardId;
        await using (var db = restarted.Factory.CreateDbContext())
            boardId = (await db.Collections.SingleAsync(c => c.Name == "Nature")).Id;
        await restarted.Curation.RenameBoardAsync(boardId, "Nature (after rebuild)");

        Assert.Contains("Nature (after rebuild)",
            await ArchiveTestProjection.ProjectAsync(new TestDbContextFactory(indexPath)));
    }

    [Fact]
    public async Task A_legacy_project_opened_without_upgrading_keeps_working_in_place()
    {
        var folder = SeedLegacyProject("Stay Legacy");
        var machine = Machine("appdata-1", "device-1");

        machine.Projects.Open(folder);
        await machine.Factory.EnsureCreatedAsync(); // the user said "not now"

        Assert.True(File.Exists(Path.Combine(folder, "hoard.db"))); // still the in-folder database
        Assert.Equal(1, HoardProject.Peek(folder).FormatVersion);
        var stats = Assert.IsType<ProjectStats>(await ProjectStatsReader.ReadAsync(folder, machine.Projects.AppPaths));
        Assert.Equal(2, stats.Images);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private sealed record TestMachine(ProjectManager Projects, ProjectDbContextFactory Factory, ArchiveLog Archive, CurationService Curation);

    /// <summary>The production object graph in miniature: per-machine app data + device id, storage
    /// following the open project — exactly how AddHoardCore wires it.</summary>
    private TestMachine Machine(string appDataName, string deviceId)
    {
        var appPaths = new AppPaths(Path.Combine(_dir, appDataName));
        var projects = new ProjectManager(appPaths);
        var archive = new ArchiveLog(deviceId, opsRoot: () => projects.Current?.OpsRoot);
        var factory = new ProjectDbContextFactory(projects, archive);
        var store = new ProjectMediaStore(projects);
        return new TestMachine(projects, factory, archive, new CurationService(factory, store, null, archive));
    }

    private static async Task Import(TestMachine machine, HoardProject project)
    {
        var store = new ContentAddressedStore(project.StoreRoot);
        var ingest = new IngestService(machine.Factory, store, new[] { new FakePins() }, null, machine.Archive);
        await ingest.ImportAsync("https://pinterest.com/jane/", new ConnectorOptions(), null);
    }

    /// <summary>A pre-P3 project folder: a v1 marker (no format/id fields) and a seeded hoard.db.</summary>
    private string SeedLegacyProject(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, HoardProject.MarkerFileName),
            JsonSerializer.Serialize(new { name, schemaVersion = 1 }));

        var factory = new TestDbContextFactory(Path.Combine(folder, "hoard.db"));
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        // Fresh-model DBs must carry the version stamp EnsureCreated normally gets from the initializer,
        // else the additive upgrades re-run onto a full model. (Legacy here means the FOLDER layout — the
        // schema itself can be current; format and schema versions are independent.)
        db.Database.ExecuteSqlRaw($"PRAGMA user_version = {Hoard.Core.Metadata.SchemaInitializer.LatestSchemaVersion};");
        var t = DateTimeOffset.UtcNow.AddDays(-10);
        var alpha = new Collection { Name = "Alpha", SourceConnector = "pinterest", SourceBoardId = "bA", SourceUrl = "https://p/bA", CreatedAt = t };
        var source = new CollectionSource { Collection = alpha, SourceConnector = "pinterest", SourceBoardId = "bA", SourceUrl = "https://p/bA", AddedAt = t.AddMinutes(1) };
        var a1 = LegacyAsset("legacy-sha-1", t.AddMinutes(2));
        var a2 = LegacyAsset("legacy-sha-2", t.AddMinutes(3));
        db.Collections.Add(alpha);
        db.CollectionSources.Add(source);
        db.Assets.AddRange(a1, a2);
        db.CollectionItems.AddRange(
            new CollectionItem { Collection = alpha, Asset = a1, CollectionSource = source, AddedAt = t.AddMinutes(4) },
            new CollectionItem { Collection = alpha, Asset = a2, AddedAt = t.AddMinutes(5) });
        db.SaveChanges();
        return folder;
    }

    private static Asset LegacyAsset(string sha, DateTimeOffset importedAt) => new()
    {
        Sha256 = sha,
        RelativePath = $"{sha[..2]}/{sha[2..4]}/{sha}.jpg",
        MimeType = "image/jpeg",
        Kind = MediaKind.Image,
        Bytes = 100,
        SourceConnector = "pinterest",
        SourceId = $"pin-{sha}",
        SourceUrl = $"https://i.pinimg.com/{sha}.jpg",
        ImportedAt = importedAt,
    };

    private sealed class FakePins : ISourceConnector
    {
        public string Name => "pinterest";
        public bool CanHandle(string url) => true;

        public async Task DownloadAsync(
            string url, ConnectorOptions options, IProgress<string>? log,
            Func<SourceMediaItem, CancellationToken, Task> onItem, CancellationToken ct)
        {
            var temp = Path.Combine(Path.GetTempPath(), "hoard-v2-dl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var (content, i) in new[] { "AAA", "BBB", "CCC" }.Select((c, i) => (c, i)))
                {
                    var file = Path.Combine(temp, $"{i}_{content}.jpg");
                    File.WriteAllText(file, content);
                    await onItem(new SourceMediaItem
                    {
                        FilePath = file,
                        Connector = Name,
                        SourceId = $"pin{i}",
                        BoardName = "Nature",
                        BoardId = "Nature",
                        BoardUrl = "https://pinterest.com/jane/nature/",
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
