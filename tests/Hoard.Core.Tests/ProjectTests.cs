using Hoard.Core.Projects;
using Xunit;

namespace Hoard.Core.Tests;

public class ProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-proj-test", Guid.NewGuid().ToString("N"));

    public ProjectTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Create_writes_marker_and_data_dirs()
    {
        var folder = Path.Combine(_dir, "My Archive");
        var project = HoardProject.Create(folder);

        Assert.True(HoardProject.IsProject(folder));
        Assert.True(File.Exists(project.MarkerPath));
        Assert.True(Directory.Exists(project.StoreRoot));
        // A v2 archive folder is static content only — derived caches live in per-machine app data (P4).
        Assert.False(Directory.Exists(project.LogsRoot));
        Assert.False(Directory.Exists(project.ThumbnailsRoot));
        Assert.Equal("My Archive", project.Name);          // derived from folder name
        Assert.Equal(Path.Combine(folder, "hoard.db"), project.DatabasePath);
    }

    [Fact]
    public void Create_twice_in_same_folder_throws()
    {
        var folder = Path.Combine(_dir, "dup");
        HoardProject.Create(folder);
        Assert.Throws<InvalidOperationException>(() => HoardProject.Create(folder));
    }

    [Fact]
    public void Open_non_project_folder_throws()
    {
        var folder = Path.Combine(_dir, "not-a-project");
        Directory.CreateDirectory(folder);
        Assert.Throws<InvalidOperationException>(() => HoardProject.Open(folder));
    }

    [Fact]
    public void Open_preserves_custom_name()
    {
        var folder = Path.Combine(_dir, "named");
        HoardProject.Create(folder, "Custom Name");
        Assert.Equal("Custom Name", HoardProject.Open(folder).Name);
    }

    [Fact]
    public void Project_id_is_minted_at_create_and_stable_across_opens()
    {
        var folder = Path.Combine(_dir, "with-id");
        var created = HoardProject.Create(folder);

        Assert.NotEqual(default, created.Id);
        Assert.Equal(created.Id, HoardProject.Open(folder).Id);
        Assert.Equal(created.Id, HoardProject.Open(folder).Id); // and again — never re-minted
    }

    [Fact]
    public void Open_backfills_an_id_into_a_legacy_marker_once()
    {
        // A marker written by an older build: valid JSON, no "id".
        var folder = Path.Combine(_dir, "legacy-id");
        HoardProject.Create(folder);
        File.WriteAllText(Path.Combine(folder, HoardProject.MarkerFileName),
            """{ "name": "Legacy", "schemaVersion": 1 }""");

        var first = HoardProject.Open(folder);
        Assert.NotEqual(default, first.Id);
        Assert.Equal("Legacy", first.Name); // backfill preserves the stored name

        Assert.Equal(first.Id, HoardProject.Open(folder).Id); // persisted — later opens agree
    }

    [Fact]
    public void Open_tolerates_a_corrupt_marker_by_deriving_the_name()
    {
        var folder = Path.Combine(_dir, "Corrupt Marker");
        HoardProject.Create(folder, "Old Name");
        File.WriteAllText(Path.Combine(folder, HoardProject.MarkerFileName), "{ this is not valid json ");

        var project = HoardProject.Open(folder); // still a project (marker present), just unreadable
        Assert.Equal("Corrupt Marker", project.Name); // falls back to the folder name instead of throwing
    }

    [Fact]
    public void Adopt_recreates_a_missing_marker_for_a_data_folder()
    {
        var folder = Path.Combine(_dir, "lost-marker");
        HoardProject.Create(folder, "Recovered");
        File.Delete(Path.Combine(folder, HoardProject.MarkerFileName));
        Assert.False(HoardProject.IsProject(folder));            // no marker → not openable normally
        Assert.True(HoardProject.LooksLikeProjectFolder(folder)); // but the store/db give it away

        var project = HoardProject.Adopt(folder);

        Assert.True(HoardProject.IsProject(folder));             // marker rewritten
        Assert.Equal("lost-marker", project.Name);              // name derived from the folder
    }

    [Fact]
    public void Adopt_refuses_a_folder_with_no_project_data()
    {
        var folder = Path.Combine(_dir, "just-files");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "hi");
        Assert.Throws<InvalidOperationException>(() => HoardProject.Adopt(folder));
    }

    [Fact]
    public void Manager_adopt_opens_a_marker_less_project()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var folder = Path.Combine(_dir, "adopt-me");
        new ProjectManager(appPaths).Create(folder); // create the data…
        File.Delete(Path.Combine(folder, HoardProject.MarkerFileName)); // …then lose the marker

        var manager = new ProjectManager(appPaths);
        var project = manager.Adopt(folder);

        Assert.Equal(folder, Path.GetFullPath(project.Root));
        Assert.Equal(folder, manager.Current!.Root);
        Assert.Contains(folder, manager.RecentProjects);
    }

    [Fact]
    public void Manager_prunes_recents_whose_folder_is_gone()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var keep = Path.Combine(_dir, "still-here");
        var gone = Path.Combine(_dir, "moved-away");
        var m1 = new ProjectManager(appPaths);
        m1.Create(keep);
        m1.Create(gone);
        Directory.Delete(gone, recursive: true); // moved/deleted outside the app

        // A fresh manager drops the vanished folder from recents on load.
        var m2 = new ProjectManager(appPaths);
        Assert.Equal(new[] { keep }, m2.RecentProjects.ToArray());
    }

    [Theory]
    [InlineData("Pinterest Archive")]
    [InlineData("my_board-2026")]
    public void ValidateName_accepts_good_names(string name)
        => Assert.Null(HoardProject.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("nul")]
    public void ValidateName_rejects_bad_names(string name)
        => Assert.NotNull(HoardProject.ValidateName(name));

    [Fact]
    public void Create_with_illegal_name_throws_before_touching_disk()
    {
        Assert.Throws<ArgumentException>(() => HoardProject.Create(Path.Combine(_dir, "x"), "bad/name"));
    }

    [Fact]
    public void Manager_tracks_recents_most_recent_first_and_persists()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");

        var m1 = new ProjectManager(appPaths);
        m1.Create(a);
        m1.Create(b);
        Assert.Equal(b, m1.Current!.Root);
        Assert.Equal(new[] { b, a }, m1.RecentProjects.ToArray());

        // A fresh manager reads persisted settings and re-opens the most recent valid project.
        var m2 = new ProjectManager(appPaths);
        Assert.Null(m2.Current); // not opened until asked
        Assert.Equal(b, m2.OpenLastOrNull()!.Root);
    }

    [Fact]
    public void RemoveFromRecents_forgets_without_deleting_files()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var folder = Path.Combine(_dir, "keepme");
        var manager = new ProjectManager(appPaths);
        manager.Create(folder);

        manager.RemoveFromRecents(folder);

        Assert.Empty(manager.RecentProjects);
        Assert.True(Directory.Exists(folder));               // files untouched
        Assert.True(HoardProject.IsProject(folder));
    }

    [Fact]
    public void DeleteProject_removes_folder_and_forgets_it()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var folder = Path.Combine(_dir, "deleteme");
        var manager = new ProjectManager(appPaths);
        manager.Create(folder);

        manager.DeleteProject(folder);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(manager.RecentProjects);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void DeleteProject_recovers_a_half_deleted_remnant()
    {
        // Simulate a folder where the marker was already removed (e.g. an interrupted delete) but the
        // database/store remain. It should still be recognized and cleanable, not stranded.
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var folder = Path.Combine(_dir, "remnant");
        var manager = new ProjectManager(appPaths);
        manager.Create(folder);
        File.Delete(Path.Combine(folder, "hoard.project.json")); // marker gone → not IsProject anymore
        Assert.False(HoardProject.IsProject(folder));
        Assert.True(HoardProject.LooksLikeProjectFolder(folder)); // but still recognizable via store/

        manager.DeleteProject(folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteProject_refuses_non_project_folder()
    {
        var appPaths = new AppPaths(Path.Combine(_dir, "appdata"));
        var notAProject = Path.Combine(_dir, "important-stuff");
        Directory.CreateDirectory(notAProject);
        File.WriteAllText(Path.Combine(notAProject, "precious.txt"), "do not delete");

        var manager = new ProjectManager(appPaths);
        Assert.Throws<InvalidOperationException>(() => manager.DeleteProject(notAProject));
        Assert.True(Directory.Exists(notAProject)); // guard kept it safe
    }

    [Fact]
    public void Open_refuses_an_archive_written_by_a_newer_build()
    {
        // The forward format gate: half-understanding a newer archive would emit/replay ops under
        // unknown semantics and silently diverge the fleet — refusal with a clear message is the
        // only safe behaviour.
        var folder = Path.Combine(_dir, "FromTheFuture");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "hoard.project.json"),
            $$"""{"name":"Future","id":"{{Guid.NewGuid()}}","format":{{HoardProject.CurrentFormatVersion + 1}}}""");

        var ex = Assert.Throws<InvalidOperationException>(() => HoardProject.Open(folder));
        Assert.Contains("newer version of Hoard", ex.Message);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
