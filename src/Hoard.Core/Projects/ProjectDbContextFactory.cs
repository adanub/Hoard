using Hoard.Core.Metadata;
using Hoard.Core.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Projects;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that targets the currently open project's database.
/// Each created context points at <c>ProjectManager.Current.DatabasePath</c>, so switching projects
/// transparently switches databases.
/// </summary>
public sealed class ProjectDbContextFactory : IDbContextFactory<HoardDbContext>
{
    private readonly ProjectManager _projects;
    private readonly ArchiveLog? _archive;
    private readonly ILogger<ProjectDbContextFactory>? _logger;

    public ProjectDbContextFactory(
        ProjectManager projects, ArchiveLog? archive = null, ILogger<ProjectDbContextFactory>? logger = null)
    {
        _projects = projects;
        _archive = archive;
        _logger = logger;
    }

    public HoardDbContext CreateDbContext()
    {
        var project = _projects.Current
            ?? throw new InvalidOperationException("No project is open. Create or open a project first.");
        return CreateForPath(DatabasePathFor(project));
    }

    /// <summary>
    /// Where the open project's metadata database lives. Format v2 (SYNC-DESIGN.md P3): the per-machine
    /// derived index under app data, keyed by the project's stable id — SQLite never touches a network
    /// share again. Legacy v1: the folder's own <c>hoard.db</c>, until the user accepts the migration.
    /// </summary>
    private string DatabasePathFor(HoardProject project)
    {
        if (project.FormatVersion < HoardProject.CurrentFormatVersion) return project.DatabasePath;
        var stateRoot = _projects.AppPaths.ProjectStateRoot(project.Id);
        Directory.CreateDirectory(stateRoot);
        return _projects.AppPaths.IndexDbPath(project.Id);
    }

    /// <summary>A context on an explicit database file — the shared connection policy in one place.</summary>
    internal static HoardDbContext CreateForPath(string databasePath)
    {
        // Pooling=False so an idle project holds no file handle on its database — the folder stays
        // movable/zippable/deletable while the app is open (it's only locked during an active query).
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<HoardDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new HoardDbContext(options);
    }

    /// <summary>Ensure the current project's database exists and is upgraded to the current schema, then
    /// reconcile the op segments (backfill our own, catch up foreign devices' — for a fresh v2 index this
    /// IS the build: the whole archive replays into it). Call after opening/switching a project. Pass
    /// <paramref name="upgradeLegacyFormat"/> when the user accepted the one-time v1 → v2 storage
    /// migration for the current project.</summary>
    public async Task EnsureCreatedAsync(bool upgradeLegacyFormat = false, CancellationToken ct = default)
    {
        if (upgradeLegacyFormat && _archive is not null && _projects.Current is { } legacy
            && ArchiveMigration.IsRequired(legacy))
            await ArchiveMigration.MigrateAsync(legacy, _projects.AppPaths, _archive, ct);

        // A v2 folder can hold a leftover legacy DB (crash between the migration's stamp and rename, or
        // a stray from an old build) — rename it out of the way so nothing ever mistakes it for data.
        if (_projects.Current is { } current) ArchiveMigration.TidyMigratedFolder(current);

        await using var db = CreateDbContext();
        await SchemaInitializer.InitializeAsync(db, ct);

        if (_archive is not null && _projects.Current is { } project)
        {
            // Best-effort: a segment problem (an unreadable NAS, a garbled foreign file) must not fail
            // the open — it logs and retries next open/write. A partially caught-up index self-heals the
            // same way (watermarks resume exactly where they stopped).
            try
            {
                await ArchiveSync.SyncAtOpenAsync(db, project.OpsRoot, _archive, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Op segment reconcile failed at open; the project remains usable.");
            }
        }
    }
}
