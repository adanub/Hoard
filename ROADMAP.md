# Roadmap

What's planned, in rough priority order. **Forward-looking only** — completed work lives in git history
and `CHANGELOG.md`; how the app works lives in `CLAUDE.md` (with `SYNC-DESIGN.md` for storage/sync and
`DESIGN.md` for UI). Keep entries short, and delete them when they land.

## Now — user-reported pain (2026-07-27), highest priority

- **Board Sync is far too slow.** Today a Sync re-runs the full gallery-dl crawl per source: every pin
  is enumerated page-by-page even though nearly all are pre-skipped by the skip-archive, so wall time
  scales with board size instead of with what's new. Needs a real incremental design — e.g. stop the
  crawl once it hits N consecutive already-known pins (newest-first listing means new pins front-load),
  and/or a listing-only pass that fetches nothing until it knows what's missing — not a per-item check
  of the entire board every time.
- **Backup sync (folder/NAS remote) is far too slow.** A sync with a handful of changed files can take
  10+ minutes because `ArchiveReplicator` reconciles by re-enumerating and stat-ing the FULL file set
  (thousands of blobs over SMB) on every run. The archive is append-only, so the op log already knows
  exactly what's new — keep a per-remote cursor (pushed chapter lengths + a pushed-blob manifest under
  the remote config) and move only the delta; demote the full re-list to a rare explicit "verify
  backup" action, never the every-sync path.
- **Whole-project export.** Export currently works one board at a time; add an Export at the Library
  level that materialises the entire project as `dest/<Project>/<Board>/<Folder>/…` — same
  `BoardExporter` naming/incremental rules, one run.

## Next (unordered, pick by need)

- **FTS5 search** — scale-up from LIKE (additive aux table); premature while libraries are hundreds of
  pins.
- **Video poster-frame thumbnails** — video tiles show a placeholder; needs an ffmpeg strategy (not
  bundled).
- **Paging for very large boards** — `BoardViewModel` materialises a tile VM per asset; page when it
  hurts.
- **Fold or drop the denormalised `Collection.SourceBoardId`/`SourceUrl` "primary source" pointer**
  (write-mostly; two-sources-of-truth maintenance).
- **Restore edge**: if a stored `SourceUrl` was the pin page rather than direct media, restore fetches
  HTML — re-fetch via gallery-dl by pin id if this ever shows up in practice.
- **Lightbox polish**: cap the decode width (never upscale), pause the band GIF while occluded, busy
  spinner on the GIF path, gallery entry, let `MasonryPacker` own the stacked-vs-wide predicate.
- **Shell-chrome cleanups**: shared card-filter helper (trim→filter→crumb-count is triplicated),
  shared `Debouncer`, `Plural(n, noun)` formatter, atomic `JsonFile.TryLoad/Save`, per-property
  observable `UiSettings`, a `FloatingBarClearance` token for the hardcoded ~96px insets, a single
  dismissable-transient arbiter, `MasonryLayout` exposing its visible index range for GIF autoplay.
- **Popup-rooted UI doesn't inherit the interface-scale transform** (ComboBox dropdowns, tooltips at
  non-100% scale) — needs a popup-scaling strategy.
- **macOS trash implementation** for `IFileRecycler` (recycle is Windows-only; elsewhere deletes are
  permanent and the wording says so).
- **`.editorconfig`** enforcing the C# conventions in CLAUDE.md.
- Perf notes to revisit at scale: `LibraryService.GetCollectionsAsync` N+1 per board;
  `GifDecoder.Snapshot` double-encodes per frame; gallery-dl archive re-probes never-ingested items
  each import.

## Later phases

- **Phase 3 finish — segment compaction**: deferred until op-log size actually hurts (single-user). A
  `ProjectVerifier` safe orphan-sweep would need an "all devices caught up" proof first.
- **Phase 4 — mobile**: extract Core behind a `Hoard.Server` (ASP.NET Core minimal API; server-side
  ingestion since mobile can't spawn gallery-dl); Avalonia mobile client with background sync.
- **Phase 5 — capture + more connectors**: TS browser extension (in-page "save to Hoard"); more
  sources behind `ISourceConnector`.

## Shelved / dropped — don't resurrect unprompted

- **Manual collections** (shelved 2026-07-27, user call: not important). If revived: exclude
  manually-added images from `RemoveSourceAsync`'s last-source sweep.
- **S3/B2 remote (R3) + fetch-on-demand replicas (R4)** (dropped 2026-07-26): the folder remote
  reaches any mountable target; rclone/Syncthing cover object storage.
- **Tag filtering** (deprioritised): Pinterest sidecars carry no tags in practice; revisit only if a
  connector surfaces real ones.
- **Material-shader background** (dropped): don't resurrect.
