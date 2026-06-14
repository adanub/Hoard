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
        Assert.True(Directory.Exists(project.LogsRoot));
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

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
