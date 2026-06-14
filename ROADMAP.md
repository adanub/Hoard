# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-14):** Phase 1 complete. Phase 2 in progress. Tests: 35 (Core) + 19 (Desktop), green.

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

## Next up — finish Phase 2

- **Tag filtering** in the sidebar (note: Pinterest sidecars often carry few/no tags — check real data value first).
- **Manual collections** (user-created, not just source boards).
- **Curate / delete assets**, and introduce a `SyncOp` append-only log (add/remove) — this sets up Phase 3.
- **FTS5** search (deferred scale-up from the current LIKE; additive — an aux table, no schema rework).

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
- `LibraryService.GetCollectionsAsync` emits a correlated COUNT per collection (N+1 at large board counts).
- Video poster-frame thumbnails (ffmpeg) not done — video tiles show a placeholder; "Open file" plays
  externally. ffmpeg isn't bundled.
- No paging: `LibraryViewModel` materialises a tile VM per asset (cheap via virtualisation, but the VM list
  is full) — page when libraries get very large.
- Optional: add an `.editorconfig` to enforce the C# formatting conventions documented in `CLAUDE.md`.

## Needs runtime verification (can't launch the GUI in-session)

Build + tests cover logic/compile/layout-math, but the user should confirm at runtime: masonry scroll feel
+ recycling, GIF playback/animation correctness, and that memory drops when GIFs are stopped / unloaded /
navigated away from. Diagnose via `hoard.log`.
