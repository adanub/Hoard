using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Hoard.Core.Metadata;

/// <summary>
/// Brings a project database up to the current schema. A fresh database is created from the full EF model;
/// an existing one (possibly built by an older app version) is patched with the additive upgrades it
/// predates. Schema state is tracked with SQLite's <c>user_version</c> pragma — a lightweight alternative
/// to full migrations that fits this app's many independent, user-owned project databases. All upgrades
/// are additive and idempotent, so this is safe to run on every open.
/// </summary>
public static class SchemaInitializer
{
    /// <summary>Bump this and add a matching <see cref="Upgrades"/> entry whenever the model gains additive objects.</summary>
    public const long LatestSchemaVersion = 2;

    /// <summary>
    /// Ordered additive patches applied to a pre-existing database whose <c>user_version</c> is below the
    /// target. Each is plain DDL that must match what EF Core's model would create for the same objects.
    /// </summary>
    private static readonly (long Version, string Sql)[] Upgrades =
    {
        // v1 — the append-only sync log (added after the first release; see Sync/SyncLog.cs).
        (1, """
            CREATE TABLE IF NOT EXISTS "SyncOps" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncOps" PRIMARY KEY AUTOINCREMENT,
                "Op" INTEGER NOT NULL,
                "EntityType" INTEGER NOT NULL,
                "EntityKey" TEXT NOT NULL,
                "Timestamp" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SyncOps_EntityKey" ON "SyncOps" ("EntityKey");
            """),
        // v2 — soft-delete tombstones: an asset's blob is removed but the row is kept, with a note, so a
        // deletion is global, recorded, and restorable (see Library/CurationService.cs).
        (2, """
            ALTER TABLE "Assets" ADD COLUMN "DeletedAt" TEXT NULL;
            ALTER TABLE "Assets" ADD COLUMN "DeletionNote" TEXT NULL;
            """),
    };

    public static async Task InitializeAsync(HoardDbContext db, CancellationToken ct = default)
    {
        var created = await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        // WAL lets a background import write while the UI reads, avoiding "database is locked". It's a
        // persistent property of the file, so setting it once per open is enough.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);

        if (created)
        {
            // A fresh database was just built from the full current model — stamp it as up to date.
            await SetVersionAsync(db, LatestSchemaVersion, ct).ConfigureAwait(false);
            return;
        }

        var current = await GetVersionAsync(db, ct).ConfigureAwait(false);
        foreach (var (version, sql) in Upgrades)
        {
            if (version <= current) continue;
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            await SetVersionAsync(db, version, ct).ConfigureAwait(false);
        }
    }

    private static async Task<long> GetVersionAsync(HoardDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    // PRAGMA can't be parameterised, so the value must be inlined. It's an internal long (never user
    // input), and we build the command as a plain string — not an interpolated one passed straight to
    // ExecuteSqlRaw — so EF's injection analyzer (EF1002) has nothing to flag.
    private static Task SetVersionAsync(HoardDbContext db, long version, CancellationToken ct)
    {
        var sql = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture) + ";";
        return db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
