# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-14):** Phase 1 complete. Phase 2 in progress. Tests: 47 (Core) + 19 (Desktop), green.

## Done

**Phase 1 — Core + desktop MVP**
- Three-assembly solution (`Hoard.Core`, `Hoard.Ingest.GalleryDl`, `Hoard.Desktop`) + two test projects.
- Project model: launcher (create / open / switch / delete, with name validation + 10s-locked delete
  confirm), project-scoped DB + content-addressed store, recent-projects list.
- Pinterest import via bundled gallery-dl: browser cookies (incl. Firefox-fork/Zen profile detection),
  rate limiting with randomised jitter, `--download-archive` incremental re-imports, **streaming** ingest
  (per-item store + DB upsert + live grid tiles), content-hash dedup.
- Logging to terminal + `%APPDATA%/Hoard/logs/hoard.log`.

**Phase 2 — library experience (partial)**
- Search: LIKE over title/description/tags, debounced, scoped to the selected board.
- Detail overlay panel: full metadata, closable (✕ / Esc), open source / original / file.
- On-disk thumbnail cache (per project); launcher shows each project's cache size + a Clear button.
- Virtualised masonry grid (`MasonryPacker` pure math + `MasonryLayout` shell, per-column binary search).
- GIF handling: animated playback (custom SkiaSharp decoder), click-to-play in grid + detail, "GIF" badge,
  in-memory footprint tag, manual Unload button, frames downscaled to display width, refcounted
  `ResourceLease` ownership so memory tracks what's on screen.
- Curate / delete = **tombstone with undo**. Deleting (with a required note) frees the blob from disk but
  keeps the row, so the tile stays in place showing the note; the detail panel offers **Restore**, which
  re-downloads the original media directly from its saved CDN URL (no cookies/subprocess) and un-deletes it.
  Grid order is by **Pinterest pin id** (descending), fixed per pin, so re-import/restore doesn't reshuffle.
  The removal is global by construction (one row per
  unique SHA-256). Every add (ingest), remove (delete) and re-add (restore) is recorded in an **append-only
  `SyncOp` log** (`Hoard.Core/Sync/SyncLog.cs`), keyed by content SHA-256 — the foundation for Phase 3.
  Imports **honour tombstones** (a deleted item won't crawl back in) and the DB is the single authority.
- Schema versioning via `PRAGMA user_version` (`Metadata/SchemaInitializer.cs`): v1 = `SyncOps`, v2 =
  tombstone columns. Older project DBs upgrade in place; fresh ones are built from the full model.
- **gallery-dl archive is a derived projection, not a second source of truth.** Before each import the
  connector rebuilds `download-archive.db` from what the library tracks (live + tombstoned, via
  `ConnectorOptions.KnownItems`), so it can never drift from the DB or silently hide an item.

## Next up — finish Phase 2

- **Manual collections** (user-created, not just source boards) — the most valuable remaining item.
- **FTS5** search — deferred scale-up from the current LIKE (additive aux table); premature while libraries
  are small (LIKE is fine for hundreds of pins), so low priority.
- ~~**Tag filtering**~~ — **deprioritised**: confirmed against real data (78 pins) that the Pinterest
  sidecars carry no `tags`/`hashtags` field, so a tag filter would be empty. Revisit only if a connector
  surfaces real tags.

## Later phases

- **Phase 3 — cloud backup/sync:** S3-compatible blob backup (Backblaze B2 / R2 / MinIO) + sync-log replay
  for adds/removals; multi-device reconciliation.
- **Phase 4 — mobile:** extract Core behind a `Hoard.Server` (ASP.NET Core minimal API, does ingestion
  server-side since mobile can't spawn gallery-dl); Avalonia mobile client with background sync.
- **Phase 5 — capture + more connectors:** TS browser extension (in-page "save to Hoard"); more sources
  behind the same `ISourceConnector`.

## Deferred / known tech-debt

- `GifDecoder.Snapshot` double-encodes (full-res PNG → `DecodeToWidth`) per frame; a direct Skia resize
  would avoid the round-trip. Kept the safe path (off-thread, not a render hot path).
- `LibraryService.GetCollectionsAsync` emits a correlated COUNT per collection (N+1 at large board counts);
  the count includes tombstones (they still occupy the board, shown as note tiles).
- Rebuilding the gallery-dl archive from the DB drops entries for items that were downloaded but never
  ingested (e.g. videos with no ffmpeg), so those get re-probed on each import — accepted cost; add an
  "ignored items" record if it becomes noticeable.
- Restore downloads the asset's stored media URL (`SourceUrl`, which for Pinterest is the direct
  `i.pinimg.com` link the sidecar's `url` field carried) — the Restore button is disabled without one. This
  assumes that URL is the media itself (true for real Pinterest data); in the rare case the sidecar lacked
  `url` and `SourceUrl` fell back to the pin page, restore would fetch HTML — could re-fetch via gallery-dl
  on the pin id if that ever shows up.
- Video poster-frame thumbnails (ffmpeg) not done — video tiles show a placeholder; "Open file" plays
  externally. ffmpeg isn't bundled.
- No paging: `LibraryViewModel` materialises a tile VM per asset (cheap via virtualisation, but the VM list
  is full) — page when libraries get very large.
- Optional: add an `.editorconfig` to enforce the C# formatting conventions documented in `CLAUDE.md`.

## Needs runtime verification (can't launch the GUI in-session)

Build + tests cover logic/compile/layout-math, but the user should confirm at runtime: masonry scroll feel
+ recycling, GIF playback/animation correctness, and that memory drops when GIFs are stopped / unloaded /
navigated away from. Diagnose via `hoard.log`.
