# Archive format v2 — immutable archive + derived index

Design of record for the storage-layer rewrite (supersedes the two-line Phase 3 sketch in `ROADMAP.md`).
Status: **P0 + P1 implemented** — HLC/DeviceId/ProjectId primitives, schema v8 (uids + `ArchiveOps`),
live op emission from every Core write path, the coverage-aware synthesiser, and `ArchiveRebuilder`, with
round-trip equivalence proven by `ArchiveRoundTripTests` (synthesised history AND live service ops each
rebuild an equivalent DB). **P2 implemented**: per-device segment files with dual-write (two-device convergence proven by
`ArchiveConvergenceTests`). **P3 implemented**: format v2 — new projects are born v2, the index lives per
machine under app data, legacy projects migrate behind a launcher confirm, and a machine's index is fully
derivable from the archive alone (`ArchiveFormatV2Tests`). **NAS multi-machine works from here.**
**P4 implemented except compaction** (see the P4 bullet): derived caches relocated to per-machine app
data, on-demand verify-project, one apply semantics (`ArchiveRebuilder` deleted), batched index builds.
Next: segment compaction (deferred), then P5 remotes. When increments land, track them in `ROADMAP.md`
and fold the settled architecture into `CLAUDE.md`.

## Why

A project folder today is "static media plus one live SQLite database", and that one live file is the
source of every multi-machine problem:

- **WAL over a network share fails.** `hoard.db` is stamped WAL-mode on every open
  (`SchemaInitializer`), WAL needs a memory-mapped `-shm`, and SQLite documents that as unsupported over
  network filesystems. macOS smbfs refuses outright (`SQLITE_CANTOPEN` 14, reproduced against the user's
  NAS); Windows merely *happens* to work. Any live-DB fix is a band-aid — some filesystem will always
  break it.
- **Two machines can never safely share the archive**, even in a rollback journal — SQLite over SMB with
  two clients is corruption-prone by design.
- **Phase 3 (sync) needs a replayable op log anyway**, and the current `SyncOps` table is a stub
  (kind + sha + timestamp — not replayable, nothing reads it).

One architecture dissolves all three: make the archive **100 % immutable/append-only files**, and demote
SQLite to a **disposable, per-machine index** derived from them. Immutable blobs + append-only segments is
the access pattern that is safe on SMB, NFS, Syncthing, Dropbox and S3 alike (the same reason git remotes
and restic repositories work on dumb file shares) — and it *is* the sync foundation: a NAS folder becomes
the first sync remote, a passive one.

## Target model

**Project folder = the archive. Nothing in it is ever modified in place.**

```
<project>/
  hoard.project.json      # marker: name + ProjectId (GUID, new) + formatVersion 2
  store/                  # content-addressed blobs, SHA-256 named — unchanged
  ops/
    <deviceId>.jsonl      # append-only op segments, ONE WRITER EACH (see below)
```

**Per-machine (app-data, keyed by ProjectId) = everything live or derived:**

```
%APPDATA%/Hoard/projects/<projectId>/
  index.db                # the SQLite metadata DB — now a rebuildable materialised view
  thumbnails/             # decode cache (faster off-NAS anyway)
  logs/                   # import transcripts (machine-specific)
```

`download-archive.db` disappears from the archive: it is already rebuilt from `KnownItems` before every
import, so it becomes a per-run temp file. The index keeps WAL mode — it now always lives on a local disk,
where WAL is purely good.

### Ownership rules (the load-bearing sentence)

**Per-device is only about who may *append* to which file; every op's *effect* is project-scoped.** Each
machine appends exclusively to its own `ops/<deviceId>.jsonl` (a GUID minted per install, kept in
app-data), so no two writers ever touch the same file — that is the entire concurrency story. But a
tombstone, rename, or link op applies to the whole archive on every machine once read. Tombstones in
particular are **project state, shared across all machines** — device A tombstones a pin, device B sees it
tombstoned; there is no per-machine deletion.

## The op log

### Format

JSON Lines, one op per line:

```json
{"seq":41,"hlc":"2026-07-11T13:07:02.114Z-0003-a1b2…","op":"asset.tombstoned","sha":"…","note":"dupe"}
```

- `seq` — per-device monotonic counter. `(deviceId, seq)` is the op's identity; apply is idempotent on it.
- `hlc` — hybrid logical clock: `max(wallClockUtc, lastSeenHlc + tick)`, tie-broken by deviceId. Gives a
  stable cross-device order that survives clock skew without any coordination.
- Crash safety: appends are `open → append → flush → close` per batch (no held handles — plays nicest
  with SMB caching). A torn tail (a crashed append) is bytes after the last newline, since lines carry no
  embedded newline: readers stop at the first unparsable line, and the writer **truncates the tail back to
  the last newline before appending** (else the next line would weld onto the garbage) — the torn op then
  re-lands from the authoritative table, which is where the flush derives "what's missing" from.
- **Segments rotate at a size threshold** (implemented, P4): a device's stream is cut into chapters —
  `<deviceId>.jsonl` is chapter zero (legacy archives need no migration), continuations are
  `<deviceId>.00001.jsonl`, …. The writer appends only to the highest chapter, so a chapter is CLOSED
  (name and content final, forever) merely by a higher one existing — **no file is ever renamed**, which
  is exactly the immutable-object shape S3/B2 remotes want and what lets compaction retire whole closed
  chapters. Readers concatenate a device's chapters in order; the flush watermark spans the chain.

### Op catalogue

Assets key on **SHA-256** (already content-addressed); collections get a minted **`CollectionUid` GUID**
carried in ops, because local int ids mean nothing on another machine. Pin id (`SourceId`) rides along in
payloads — this folds in the "pin-id-keyed link model" review follow-up, collapsing skip/dedup/orphan-
reattach onto natural keys.

| op | payload (beyond keys) | emitted by today's path |
|---|---|---|
| `asset.added` | full metadata: relativePath, mime, kind, w/h, bytes, sourceConnector/Id/Url, originalUrl, title, description, metadataJson (sidecar), createdAt, importedAt | `IngestService` per-item upsert |
| `asset.tombstoned` | note, when | `DeleteAssetAsync` |
| `asset.restored` | new sha/path/bytes (keyed by the OLD sha — a re-download can yield different bytes) | `RestoreAsync` |
| `asset.refetched` | same shape as restored; emitted only when the content actually changed | `RefetchAsync` |
| `asset.retagged` | the FULL resulting tag set (replacement, LWW) | tags attached to an already-held asset on re-import |
| `source.updated` | sourceUrl | the v3-era URL backfill when a real import supplies one |
| `asset.removed` | — (hard delete; no placeholder) | remove-source / delete-board sweeps |
| `collection.created` | uid, name, parentUid?, sourceConnector, sourceBoardId?, sourceUrl?, sourceSectionId? | `CreateBoardAsync`, auto-folder + section-folder creation |
| `collection.renamed` | uid, displayName | `RenameBoardAsync` |
| `collection.deleted` | uid | `DeleteBoardAsync` |
| `source.attached` | collectionUid, sourceUid, connector, boardId?, url, name? | import records a source |
| `source.removed` | sourceUid | `RemoveSourceAsync` |
| `item.linked` | sha, collectionUid, sourceUid?, sortOrder?, addedAt | import links a pin |
| `item.unlinked` | sha, collectionUid | move / sweeps |

Writers emit **granular** ops: `DeleteBoardAsync` expands its subtree into explicit `asset.removed` +
`collection.deleted` ops, so replay is mechanical — apply never re-derives business decisions.

### Convergence rules

Ops are designed **order-independent**, so per-segment sequential apply converges regardless of
interleaving:

- Set memberships (links, sources) are an observed-remove set: for a given key, the op with the greatest
  HLC wins.
- Asset lifecycle (`tombstoned` / `restored` / `removed`) is LWW by HLC on the same sha — a later restore
  beats an earlier tombstone and vice versa. Project-wide, per the ownership rule above.
- Field updates (`collection.renamed`) are LWW per field.
- Concurrent same-content import on two devices: both emit `asset.added` for the same sha (idempotent
  upsert) and their own `item.linked`; the blob write is temp-file + rename of identical bytes — safe.

### Blob lifecycle

Unchanged ordering: the blob lands in `store/` **before** its `asset.added` op is appended (an op implies
its blob exists — same invariant as today's "ingest items are atomic once the blob lands"). Tombstone
frees the blob (restorable by re-download — current semantics); hard remove recycles it. In shared-folder
mode there is one store, so whichever device applies the op locally sees the store already updated by the
device that wrote it; a replicated-store mode (Phase 3 remotes) frees per replica on apply.

## Open flow

1. Read the marker → ProjectId.
2. Open (or create) the local `index.db` for that ProjectId.
3. Enumerate `ops/*.jsonl`; each segment has a **watermark** (applied byte offset + seq) stored in the
   index. Apply any tail beyond the watermark. First open on a new machine = full replay.
4. UI opens. During a session, own ops apply to the index and append to the segment as one logical step;
   foreign segments are re-checked on open and after each import/sync (single user — no live tailing).

Index **schema versioning gets simpler, not harder**: the additive-DDL upgrade chain
(`SchemaInitializer` v1…v7) exists because `hoard.db` is user data. An index is not — bump the index
schema version and the app **drops and rebuilds from the op log**. The DDL chain is kept only to read old
DBs during migration.

## Migration (one-way, per project, behind a ConfirmSheet)

Implemented in `Sync/ArchiveMigration`, offered on open of a formatVersion-1 project (the launcher's
confirm; Cancel opens in the legacy layout, and the offer returns next time). Steps: upgrade `hoard.db`
in place as today (≤ v8) → **synthesise the initial op log** (mint uids; HLCs follow the rows' own
timestamps; tombstoned assets emit `added` + `tombstoned` pairs so tombstones survive as project state;
existing stub `SyncOps` are redundant to the rows and ignored) → write this device's full
`ops/<deviceId>.jsonl` → **copy** the now op-complete DB to the app-data index (cheaper than a rebuild,
provably equivalent) → rename `hoard.db` → `hoard.db.pre-v2.bak` (user-deletable rollback) → stamp the
marker `format: 2`. Nothing destructive happens before the final rename+stamp. `thumbnails/`, `logs/` and
`download-archive.db` stay in the folder until P4 relocates them (derived data — harmless there). A
NAS-hosted legacy project must migrate from a machine that can open its DB (Windows) — the limit the
migration itself removes. Caveat: already-shipped OLD builds don't check the format field; opening a v2
folder with one would create a fresh empty `hoard.db` beside the archive (harmless to the data — v2 apps
ignore it).

## What survives, what changes, what dies

- **Unchanged:** the blob store; connectors + streaming ingest; the entire UI and read paths
  (`LibraryService` queries the index exactly as it queries `hoard.db` today); thumbnail logic (path moves).
- **Changed:** every write path (`IngestService`, `CurationService`) emits catalogue ops instead of only
  DB side-effects; `ProjectManager`/`ProjectDbContextFactory` point at app-data; open runs catch-up.
- **Dies:** WAL-over-SMB as a failure mode; the `SyncLog` stub; `download-archive.db` as an on-disk file;
  eventually the additive-DDL chain (migration-only); the deferred "adaptive journal mode" band-aid —
  never built.

## Increments (each shippable and testable)

- **P0 — ratify + primitives.** ✅ Done: ProjectId in the marker (backfilled on legacy opens); DeviceId in
  app-data (`Sync/DeviceIdentity`); `Sync/HybridClock` + tests.
- **P1 — replayable ops behind the current model.** ✅ Done: schema v8 (uids + `ArchiveOps`), the full op
  catalogue emitted by `Sync/ArchiveLog` from every `IngestService`/`CurationService` write path (same
  SaveChanges as the change), `Sync/ArchiveRebuilder`, and the **coverage-aware**
  `Sync/ArchiveOpSynthesiser` (idempotent — fills only the log's gaps, so it can run at any point in a
  DB's life; it IS the migration path). Round-trip equivalence proven both ways by
  `ArchiveRoundTripTests`. Not yet wired: nothing *reads* the log in production, and synthesis isn't run
  automatically — that's P3's migration step.
- **P2 — ops to files.** ✅ Done: `Sync/ArchiveSegments` (JSONL format, torn-tail repair),
  `ArchiveLog.FlushSegmentAsync` (dual-write derived from the authoritative table after each service
  write — self-healing, never fails the user's operation), and `Sync/ArchiveSync` (segment catch-up
  applied to the live DB — all segments' pending ops merged into ONE (HLC, device, seq)-ordered stream,
  since a device only references entities it has already observed, so HLC order is causally consistent
  while per-device order would drop cross-device references; one save per op so the watermark — MAX seq
  per device in `ArchiveOps` — advances atomically with each effect; wired into
  `ProjectDbContextFactory.EnsureCreatedAsync`, i.e.
  every project open, as best-effort). Two-device convergence + crash-safety proven by
  `ArchiveConvergenceTests`. Note: with the DB still living in the project folder, a NAS project is
  still gated by the WAL-over-SMB limit on macOS — P3's index move is what lifts that.
- **P3 — flip authority.** ✅ Done: the marker carries the archive `format` (1 legacy / 2 immutable);
  `ProjectDbContextFactory` routes v2 projects to `appData/projects/<projectId>/index.db` (SQLite never
  touches a network share again) and legacy v1 to the folder DB until the user upgrades;
  `Sync/ArchiveMigration` (see the migration section); catch-up now includes the machine's OWN segment
  (free in steady state via the watermark) so a fresh or wiped index rebuilds from the archive alone —
  with `ArchiveLog.ObserveOwnSeq` keeping the seq counter ahead of the replayed history. New projects are
  born v2. Deleting a project also removes its per-machine derived state; renames/moves are free (state
  keyed by ProjectId). **NAS multi-machine works from here.** Proven by `ArchiveFormatV2Tests`.
- **P4 — hygiene + scale.** ✅ Done except compaction: derived data relocated out of the folder —
  `thumbnails/` and `logs/` live under this machine's `appData/projects/<projectId>/` and
  `download-archive.db` beside them (rebuilt before every import; the open-time tidy sweeps/moves what
  older builds left in a v2 folder, so the archive folder is truly marker + store + ops); an on-demand
  **verify project** (`Library/ProjectVerifier` — re-hash every live blob + sweep unreferenced files;
  **report-only**, because on a shared archive a blob can precede this machine's catch-up of its op, so
  an automatic orphan delete could destroy a sibling device's fresh import); the review-deferred items
  all landed — the first index build commits in batched transactions (per-op SaveChanges kept inside so
  later ops' lookups see earlier rows; ~500 ops per fsync instead of one each), `ArchiveRebuilder` is
  DELETED and rebuilds go through `ArchiveSync.CatchUpAsync` (one apply semantics; the round-trip tests
  now exercise the real segments→catch-up path), op payload `relativePath`s are canonicalised to forward
  slashes at emission, launcher cards say "not opened on this computer yet" instead of zero counts for
  an unindexed v2 project, the Adopt path re-routes through the normal open (so it now gets the
  migration offer), and `HoardProject` has exactly one marker writer serialising project state.
  **Segment rotation is in** (see the op-log section: size-cut chapters, never renamed, closed = a
  higher chapter exists — the S3-shaped prerequisite). **Remaining: compaction** (a compacted snapshot
  segment, safe once every known device has applied the retired chapters — single-user, so deferred
  until log size hurts).
- **P5 — remotes (Phase 3 proper).** The same format replicated to a dumb remote; the mobile head
  reuses all of it. The archive is already remote-shaped — immutable
  sha-named blobs, per-device append-only chapters that are sealed once a higher one exists — so a
  remote is just *another copy of the archive files*, and sync is file-set reconciliation, not a
  protocol. Sub-increments:
  - **R0 — the remote abstraction.** ✅ Done: `Sync/IRemoteStore`: list (path+length), download, upload
    (atomic replace), read/write text — nothing archive-specific. First implementation is a
    **filesystem remote** (any mounted path: a backup drive, an rclone/Syncthing mount), which is both
    the test harness for the engine and a real feature (push a replica to a dumb folder).
  - **R1 — the replication engine.** ✅ Done: `Sync/ArchiveReplicator`: push/pull between the local archive
    folder and an `IRemoteStore`, replicating exactly the archive proper (marker + `store/` + `ops/`,
    never `.bak`s or strays). Rules: **blobs before segments** on push (op-implies-blob must hold
    remotely), segments converge by **length-max** (append-only files: whichever copy is longer wins —
    closed chapters are equal-or-absent, only the active chapter grows), the remote marker's ProjectId
    is verified before any transfer (refuse to mix two archives), and pull never deletes local state
    (deletion propagates through ops at catch-up, not through file sync). After a pull, the normal
    open-time catch-up applies whatever arrived.
  - **R2 — UI.** ✅ Done: the Library ＋ menu's **Backup** sheet — per-project remote configuration
    (`Sync/RemoteConfig`, stored per-machine in app-data project state — a remote is a machine's
    relationship to the archive, not archive state), and **Sync now** → `Sync/RemoteSync.SyncAsync`
    (pull → apply to the index via the normal catch-up, so the UI refreshes immediately → push), with
    live progress, import-gating, and an inside-the-project folder guard.
  - **R3 (S3/B2 `IRemoteStore`) and R4 (fetch-on-demand replicas) — DROPPED (2026-07-26), judged
    unnecessary.** The folder remote already reaches any target that can be mounted (backup drive,
    rclone/Syncthing to object storage), so P5 is complete at R0–R2. Don't resurrect without a new
    driving need.

## Risks & accepted trade-offs

- **SMB client caching** may serve a stale segment tail from another machine → re-stat/reopen on open and
  import-end; worst case is a stale view until next open, which converges. Benign.
- **Clock skew** shifts LWW outcomes; HLC bounds it and single-user use makes conflicts rare. Accepted.
- **Op-log size:** payloads carry full sidecars, so the log is the same order of magnitude as today's DB;
  segments compress well and compaction (P4) bounds it. Accepted.
- **Simultaneous imports from two machines into one NAS archive are safe by construction** (disjoint
  segment files, idempotent blob writes) — but both machines re-crawling the same board duplicates
  download effort until each reads the other's ops. Accepted (single user).
- **A live archive folder must not be partially synced by third-party tools mid-write** (e.g. Syncthing
  copying a blob before its op is fine — op-implies-blob only holds per device). Ordering rule: sync blobs
  before segments if this is ever automated; P5 remotes do this natively.
