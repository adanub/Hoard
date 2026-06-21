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
    /// <summary>Bump this and add a matching <see cref="Upgrades"/> entry whenever the model gains additive objects.
    /// (v5 and v6 are data-only steps — the attribution backfill and the stale-tombstone repair — with no DDL.)</summary>
    public const long LatestSchemaVersion = 7;

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
        // v3 — board merge: a local board can gather pins from several Pinterest source boards. "DisplayName"
        // is a local rename override (keeps the source name in "Name"); "CollectionSources" is the authoritative
        // many-sources list (see Domain/CollectionSource.cs). Existing single-source boards are backfilled into
        // it from their denormalised Source* columns so re-import and the merge list keep working.
        (3, """
            ALTER TABLE "Collections" ADD COLUMN "DisplayName" TEXT NULL;
            CREATE TABLE IF NOT EXISTS "CollectionSources" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CollectionSources" PRIMARY KEY AUTOINCREMENT,
                "CollectionId" INTEGER NOT NULL,
                "SourceConnector" TEXT NOT NULL,
                "SourceBoardId" TEXT NULL,
                "SourceUrl" TEXT NOT NULL,
                "Name" TEXT NULL,
                "AddedAt" TEXT NOT NULL,
                CONSTRAINT "FK_CollectionSources_Collections_CollectionId" FOREIGN KEY ("CollectionId") REFERENCES "Collections" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CollectionSources_CollectionId_SourceConnector_SourceBoardId" ON "CollectionSources" ("CollectionId", "SourceConnector", "SourceBoardId");
            INSERT INTO "CollectionSources" ("CollectionId", "SourceConnector", "SourceBoardId", "SourceUrl", "Name", "AddedAt")
                SELECT "Id", "SourceConnector", "SourceBoardId", COALESCE("SourceUrl", ''), "Name", "CreatedAt"
                FROM "Collections"
                WHERE ("SourceBoardId" IS NOT NULL OR "SourceUrl" IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM "CollectionSources" cs WHERE cs."CollectionId" = "Collections"."Id");
            """),
        // v4 — per-pin provenance: which merged source a board link came from, so a source can be un-merged
        // together with its own images (see Domain/CollectionItem.cs). Nullable FK with ON DELETE SET NULL so
        // removing a source without its images just un-attributes its links. SQLite permits adding a column
        // with a REFERENCES clause as long as its default is NULL (it is).
        (4, """
            ALTER TABLE "CollectionItems" ADD COLUMN "CollectionSourceId" INTEGER NULL REFERENCES "CollectionSources" ("Id") ON DELETE SET NULL;
            CREATE INDEX IF NOT EXISTS "IX_CollectionItems_CollectionSourceId" ON "CollectionItems" ("CollectionSourceId");
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

        // v5 — data-only: attribute pins imported before per-pin provenance (v4) to their source, so removing a
        // source removes its images on legacy data too. Runs once (any DB that predates v5).
        if (current < 5)
        {
            await SourceAttributionBackfill.RunAsync(db, ct).ConfigureAwait(false);
            await SetVersionAsync(db, 5, ct).ConfigureAwait(false);
        }

        // v6 — data-only repair: an earlier build wrongly *tombstoned* a board/source's images on removal
        // (instead of hard-deleting them). Those tombstones act as a blacklist that blocks re-importing the same
        // board — not what was intended. Remove them outright (their blobs were already freed); a DELETE on
        // Assets cascades the links via the FK. They're identifiable by the auto-generated deletion note;
        // per-image deletes use a user-typed note and are deliberately kept (they ARE the intended blacklist).
        if (current < 6)
        {
            // Match the exact auto-note shape `Removed with source “name”` (note the curly “ U+201C) so a
            // user-typed per-image delete note that merely starts with these words can't be purged.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"Assets\" WHERE \"DeletedAt\" IS NOT NULL " +
                "AND (\"DeletionNote\" LIKE 'Removed with source “%' OR \"DeletionNote\" LIKE 'Removed with board “%');",
                ct).ConfigureAwait(false);
            await SetVersionAsync(db, 6, ct).ConfigureAwait(false);
        }

        // v7 — nested folders (Pinterest sections as child Collections, via the long-present "ParentId").
        // "SourceSectionId" records a child folder's source section id so a re-import re-finds it. Run as a
        // *guarded* add rather than a blind ALTER: SQLite ADD COLUMN has no IF NOT EXISTS, and a fresh-model DB
        // can be used to simulate an older one in tests, so adding it unconditionally would throw "duplicate
        // column". Skip when the column already exists (fresh model) and add it only on a genuinely older DB.
        if (current < 7)
        {
            if (!await ColumnExistsAsync(db, "Collections", "SourceSectionId", ct).ConfigureAwait(false))
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Collections\" ADD COLUMN \"SourceSectionId\" TEXT NULL;", ct).ConfigureAwait(false);
            await SetVersionAsync(db, 7, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ColumnExistsAsync(HoardDbContext db, string table, string column, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            // table_info yields one row per column: (cid, name, type, notnull, dflt_value, pk) — name is index 1.
            cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
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
