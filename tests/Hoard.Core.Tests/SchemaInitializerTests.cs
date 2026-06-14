using Hoard.Core.Domain;
using Hoard.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hoard.Core.Tests;

public class SchemaInitializerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hoard-schema-test", Guid.NewGuid().ToString("N"));
    private readonly TestDbContextFactory _dbFactory;

    public SchemaInitializerTests()
    {
        Directory.CreateDirectory(_dir);
        _dbFactory = new TestDbContextFactory(Path.Combine(_dir, "hoard.db"));
    }

    [Fact]
    public async Task Fresh_database_is_stamped_at_the_latest_version_and_can_log_ops()
    {
        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db);

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
            db.SyncOps.Add(new SyncOp { Op = SyncOpKind.Add, EntityType = SyncEntityType.Asset, EntityKey = "abc" });
            await db.SaveChangesAsync(); // table exists → succeeds
            Assert.Equal(1, await db.SyncOps.CountAsync());
        }
    }

    [Fact]
    public async Task Legacy_database_is_upgraded_through_every_version_in_place()
    {
        // Simulate the original release's schema: full model, then strip everything added since (the
        // SyncOps table and the tombstone columns) and rewind user_version to 0, as an old build left it.
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"SyncOps\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Assets\" DROP COLUMN \"DeletedAt\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Assets\" DROP COLUMN \"DeletionNote\";");
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;");
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // applies v1 (SyncOps) + v2 (tombstone columns)

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
            // Both upgrades took effect: the sync log exists and the tombstone columns are usable.
            db.SyncOps.Add(new SyncOp { Op = SyncOpKind.Remove, EntityType = SyncEntityType.Asset, EntityKey = "xyz" });
            await db.SaveChangesAsync();
            var count = await db.Assets.CountAsync(a => a.DeletedAt != null); // query the new column → no error
            Assert.Equal(0, count);
            Assert.Equal(1, await db.SyncOps.CountAsync());
        }

        // Re-running is a harmless no-op (the version guard skips already-applied upgrades).
        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db);
        await using (var db = _dbFactory.CreateDbContext())
            Assert.Equal(1, await db.SyncOps.CountAsync());
    }

    private static async Task<long> ReadUserVersionAsync(HoardDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        finally { await conn.CloseAsync(); }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
