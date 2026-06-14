using Hoard.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Projects;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that targets the currently open project's database.
/// Each created context points at <c>ProjectManager.Current.DatabasePath</c>, so switching projects
/// transparently switches databases.
/// </summary>
public sealed class ProjectDbContextFactory : IDbContextFactory<HoardDbContext>
{
    private readonly ProjectManager _projects;

    public ProjectDbContextFactory(ProjectManager projects) => _projects = projects;

    public HoardDbContext CreateDbContext()
    {
        var project = _projects.Current
            ?? throw new InvalidOperationException("No project is open. Create or open a project first.");

        // Pooling=False so an idle project holds no file handle on its database — the folder stays
        // movable/zippable/deletable while the app is open (it's only locked during an active query).
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = project.DatabasePath,
            Pooling = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<HoardDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new HoardDbContext(options);
    }

    /// <summary>Ensure the current project's database schema exists. Call after opening/switching.</summary>
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync(ct);
        // WAL lets a background import write while the UI reads, avoiding "database is locked".
        // It's a persistent property of the file, so setting it once per open is enough.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
