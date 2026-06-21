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
        // Simulate the original release's schema: full model, then strip everything added since (the SyncOps
        // table, the tombstone columns, and the board-merge objects) and rewind user_version to 0.
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"SyncOps\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Assets\" DROP COLUMN \"DeletedAt\";");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Assets\" DROP COLUMN \"DeletionNote\";");
            await StripMergeObjectsAsync(db);
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 0;");
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // applies v1 (SyncOps) + v2 (tombstones) + v3/v4 (merge)

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

    [Fact]
    public async Task CollectionSources_table_built_by_the_v3_upgrade_matches_the_EF_model()
    {
        // Fresh DB: EF builds CollectionSources from the model. Upgraded DB: the v3 hand-written DDL builds it.
        // The stored schema text for that table + its index must be identical, or the merge model would behave
        // differently on a new vs an upgraded project DB.
        var fresh = new TestDbContextFactory(Path.Combine(_dir, "fresh.db"));
        await using (var db = fresh.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // EnsureCreated → full model, stamped v3

        var upgraded = new TestDbContextFactory(Path.Combine(_dir, "upgraded.db"));
        await using (var db = upgraded.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await StripMergeObjectsAsync(db);
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2;"); // pre-merge
        }
        await using (var db = upgraded.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // applies v3 (+ v4)

        var freshSql = await ReadObjectSqlAsync(fresh, "CollectionSources");
        var upgradedSql = await ReadObjectSqlAsync(upgraded, "CollectionSources");
        Assert.Equal(freshSql, upgradedSql);
    }

    [Fact]
    public async Task V3_backfills_a_legacy_single_source_board_into_CollectionSources()
    {
        // A pre-merge DB with a board carrying the old denormalised Source* columns…
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await StripMergeObjectsAsync(db);
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 2;");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Collections\" (\"Name\", \"SourceConnector\", \"SourceBoardId\", \"SourceUrl\", \"CreatedAt\") " +
                "VALUES ('Nature', 'pinterest', 'board-1', 'https://pinterest.com/jane/nature/', '2026-01-01T00:00:00+00:00');");
            // A board with a source board id but NO url — must still be backfilled (else re-import loses its
            // skip-archive), with the NOT NULL SourceUrl coalesced to empty.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Collections\" (\"Name\", \"SourceConnector\", \"SourceBoardId\", \"SourceUrl\", \"CreatedAt\") " +
                "VALUES ('NoUrl', 'pinterest', 'board-2', NULL, '2026-01-02T00:00:00+00:00');");
            // A purely local board (no provenance) must NOT get a source row.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Collections\" (\"Name\", \"SourceConnector\", \"SourceBoardId\", \"SourceUrl\", \"CreatedAt\") " +
                "VALUES ('Local', '', NULL, NULL, '2026-01-03T00:00:00+00:00');");
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // v3: creates the table + backfills

        await using (var db = _dbFactory.CreateDbContext())
        {
            var sources = await db.CollectionSources.OrderBy(s => s.SourceBoardId).ToListAsync();
            Assert.Equal(2, sources.Count); // the two provenance boards, not the local one

            Assert.Equal("board-1", sources[0].SourceBoardId);
            Assert.Equal("https://pinterest.com/jane/nature/", sources[0].SourceUrl);
            Assert.Equal("Nature", sources[0].Name); // the board's name is the source name

            Assert.Equal("board-2", sources[1].SourceBoardId);
            Assert.Equal("", sources[1].SourceUrl); // url-less board coalesced to empty
        }
    }

    /// <summary>
    /// Strip the board-merge objects (v3 + v4) so the merge upgrades can be re-applied to a simulated older DB.
    /// CollectionItems is rebuilt (not DROP COLUMN'd) because SQLite can't drop a foreign-key column; the empty
    /// rebuild omits indexes/FKs the assertions don't depend on.
    /// </summary>
    private static async Task StripMergeObjectsAsync(HoardDbContext db)
    {
        // v4: per-pin source FK column — rebuild CollectionItems without it.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"CollectionItems\";");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"CollectionItems\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_CollectionItems\" PRIMARY KEY AUTOINCREMENT, " +
            "\"CollectionId\" INTEGER NOT NULL, \"AssetId\" INTEGER NOT NULL, \"SortOrder\" INTEGER NOT NULL, " +
            "\"Note\" TEXT NULL, \"AddedAt\" TEXT NOT NULL);");
        // v3: the merge table + display-name override.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"CollectionSources\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Collections\" DROP COLUMN \"DisplayName\";");
    }

    [Fact]
    public async Task V5_attributes_legacy_links_in_single_and_multi_source_boards()
    {
        // Post-v4 DB (has the provenance column) with un-attributed links, as merges made before v4 left them.
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 4;");

            var single = new Collection { Name = "Single", SourceConnector = "pinterest", CreatedAt = DateTimeOffset.UtcNow };
            var sSource = new CollectionSource { Collection = single, SourceConnector = "pinterest", SourceBoardId = "b-single", SourceUrl = "u", AddedAt = DateTimeOffset.UtcNow };
            var multi = new Collection { Name = "Multi", SourceConnector = "pinterest", CreatedAt = DateTimeOffset.UtcNow };
            var mA = new CollectionSource { Collection = multi, SourceConnector = "pinterest", SourceBoardId = "b-A", SourceUrl = "ua", AddedAt = DateTimeOffset.UtcNow };
            var mB = new CollectionSource { Collection = multi, SourceConnector = "pinterest", SourceBoardId = "b-B", SourceUrl = "ub", AddedAt = DateTimeOffset.UtcNow };

            static Asset NewAsset(string sha, string boardId) => new()
            {
                Sha256 = sha, RelativePath = "ab/cd/" + sha, Kind = MediaKind.Image, SourceConnector = "pinterest",
                ImportedAt = DateTimeOffset.UtcNow,
                MetadataJson = $"{{\"board\":{{\"id\":\"{boardId}\"}}}}", // the stored sidecar carries the board id
            };
            // Un-attributed links (CollectionSourceId left null).
            db.CollectionItems.AddRange(
                new CollectionItem { Collection = single, Asset = NewAsset("s1", "b-single"), AddedAt = DateTimeOffset.UtcNow },
                new CollectionItem { Collection = single, Asset = NewAsset("s2", "b-single"), AddedAt = DateTimeOffset.UtcNow },
                new CollectionItem { Collection = multi, Asset = NewAsset("m1", "b-A"), AddedAt = DateTimeOffset.UtcNow },
                new CollectionItem { Collection = multi, Asset = NewAsset("m2", "b-B"), AddedAt = DateTimeOffset.UtcNow });
            db.CollectionSources.AddRange(sSource, mA, mB);
            await db.SaveChangesAsync();
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // runs the v5 attribution backfill

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
            // Single-source board: both links attributed to its one source.
            var sId = await db.CollectionSources.Where(s => s.SourceBoardId == "b-single").Select(s => s.Id).SingleAsync();
            Assert.Equal(2, await db.CollectionItems.CountAsync(ci => ci.CollectionSourceId == sId));
            // Merged board: each link attributed by its asset's stored board id.
            var aId = await db.CollectionSources.Where(s => s.SourceBoardId == "b-A").Select(s => s.Id).SingleAsync();
            var bId = await db.CollectionSources.Where(s => s.SourceBoardId == "b-B").Select(s => s.Id).SingleAsync();
            Assert.Equal("m1", await db.CollectionItems.Where(ci => ci.CollectionSourceId == aId).Select(ci => ci.Asset.Sha256).SingleAsync());
            Assert.Equal("m2", await db.CollectionItems.Where(ci => ci.CollectionSourceId == bId).Select(ci => ci.Asset.Sha256).SingleAsync());
        }
    }

    [Fact]
    public async Task V6_removes_stale_source_removal_tombstones_but_keeps_per_image_deletes()
    {
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 5;"); // post-v5, pre-v6

            var board = new Collection { Name = "B", SourceConnector = "pinterest", CreatedAt = DateTimeOffset.UtcNow };
            static Asset Tomb(string sha, string note) => new()
            {
                Sha256 = sha, RelativePath = "ab/cd/" + sha, Kind = MediaKind.Image, SourceConnector = "pinterest",
                ImportedAt = DateTimeOffset.UtcNow, DeletedAt = DateTimeOffset.UtcNow, DeletionNote = note,
            };
            var stale = Tomb("s1", "Removed with source “Animals”"); // auto note from the buggy tombstoning
            var perImage = Tomb("s2", "duplicate");                   // a user note — the intended blacklist
            var live = new Asset { Sha256 = "s3", RelativePath = "ab/cd/s3", Kind = MediaKind.Image, SourceConnector = "pinterest", ImportedAt = DateTimeOffset.UtcNow };
            db.CollectionItems.AddRange(
                new CollectionItem { Collection = board, Asset = stale, AddedAt = DateTimeOffset.UtcNow },
                new CollectionItem { Collection = board, Asset = perImage, AddedAt = DateTimeOffset.UtcNow },
                new CollectionItem { Collection = board, Asset = live, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // runs the v6 repair

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
            var shas = await db.Assets.Select(a => a.Sha256).ToListAsync();
            Assert.DoesNotContain("s1", shas);                      // stale source-removal tombstone removed
            Assert.Contains("s2", shas);                            // per-image delete kept (the real blacklist)
            Assert.Contains("s3", shas);                            // live asset untouched
            Assert.Equal(2, await db.CollectionItems.CountAsync()); // s1's link cascaded away
        }
    }

    [Fact]
    public async Task V7_adds_a_usable_SourceSectionId_column_to_a_pre_v7_database()
    {
        // Simulate a pre-v7 DB: full model, then drop the v7 column and rewind the version.
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Collections\" DROP COLUMN \"SourceSectionId\";");
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 6;");
        }

        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // v7 adds the column back

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
            // A child folder carrying a section id round-trips through the restored column.
            var board = new Collection { Name = "Interiors", SourceConnector = "pinterest", CreatedAt = DateTimeOffset.UtcNow };
            db.Collections.Add(board);
            await db.SaveChangesAsync();
            db.Collections.Add(new Collection
            {
                Name = "Kitchen", SourceConnector = "pinterest",
                ParentId = board.Id, SourceSectionId = "sec-1", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            var folder = await db.Collections.SingleAsync(c => c.ParentId == board.Id);
            Assert.Equal("sec-1", folder.SourceSectionId);
        }
    }

    [Fact]
    public async Task V7_is_a_no_op_when_the_column_already_exists()
    {
        // A fresh-model DB rewound below v7 still has SourceSectionId; the guarded add must not throw.
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 6;");
        }
        await using (var db = _dbFactory.CreateDbContext())
            await SchemaInitializer.InitializeAsync(db); // sees the column present → skips the ALTER, stamps v7
        await using (var db = _dbFactory.CreateDbContext())
            Assert.Equal(SchemaInitializer.LatestSchemaVersion, await ReadUserVersionAsync(db));
    }

    [Fact]
    public async Task V7_SourceSectionId_column_definition_matches_the_EF_model()
    {
        // Fresh DB: EF builds the column from the model. Upgraded DB: the hand-written v7 ALTER builds it. Column
        // ORDER legitimately differs for an ADD COLUMN (and DisplayName already diverged it at v3), so we assert
        // the column DEFINITION — type affinity + nullability + default — matches, per CLAUDE.md's DDL-parity rule.
        var fresh = new TestDbContextFactory(Path.Combine(_dir, "fresh7.db"));
        await using (var db = fresh.CreateDbContext()) await SchemaInitializer.InitializeAsync(db);

        var upgraded = new TestDbContextFactory(Path.Combine(_dir, "upgraded7.db"));
        await using (var db = upgraded.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Collections\" DROP COLUMN \"SourceSectionId\";");
            await db.Database.ExecuteSqlRawAsync("PRAGMA user_version = 6;");
        }
        await using (var db = upgraded.CreateDbContext()) await SchemaInitializer.InitializeAsync(db);

        var freshDef = await ReadColumnDefAsync(fresh, "Collections", "SourceSectionId");
        var upgradedDef = await ReadColumnDefAsync(upgraded, "Collections", "SourceSectionId");
        Assert.Equal(freshDef, upgradedDef);
        Assert.Equal("TEXT|notnull=0|dflt=null", freshDef); // and it is what EF generates for a nullable string
    }

    // The column's definition (type affinity, NOT NULL flag, default) from pragma table_info — order-independent.
    private static async Task<string> ReadColumnDefAsync(TestDbContextFactory factory, string table, string column)
    {
        await using var db = factory.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\");"; // cols: cid, name, type, notnull, dflt_value, pk
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return $"{reader.GetString(2)}|notnull={reader.GetInt32(3)}|dflt={(reader.IsDBNull(4) ? "null" : reader.GetValue(4))}";
            return "(missing)";
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<string> ReadObjectSqlAsync(TestDbContextFactory factory, string tableName)
    {
        await using var db = factory.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            // The table and its indexes, ordered by name for a stable comparison.
            cmd.CommandText =
                "SELECT sql FROM sqlite_master WHERE (name = $t OR tbl_name = $t) AND sql IS NOT NULL ORDER BY name;";
            var p = cmd.CreateParameter(); p.ParameterName = "$t"; p.Value = tableName; cmd.Parameters.Add(p);
            var parts = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                parts.Add(Normalise(reader.GetString(0)));
            return string.Join("\n", parts);
        }
        finally { await conn.CloseAsync(); }
    }

    // SQLite may store "IF NOT EXISTS" and varies whitespace; normalise both away for a fair text comparison.
    private static string Normalise(string sql) =>
        string.Join(' ', sql.Replace("IF NOT EXISTS ", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
