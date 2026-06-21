# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-21):** Phase 1 complete. Phase 2 — UI/UX redesign **integrated into the real screens** plus a
deep pass on the import/library data model. The **Projects → Library → Board** nav flow runs end to end. The
board-merge model, rename, Sync, hard-delete-to-recycle-bin, compatibility/resilience, and import-correctness
fixes landed earlier and are **committed** as `e8285d3` (schema **v6**).

**This session — nested folders (Pinterest sections):** a section/sub-folder is a **child `Collection`** (via the
long-present `ParentId`); schema bumped to **v7** (additive `Collection.SourceSectionId`, guarded add).
**Phase 1 (Core, done + tested):** `GetCollectionsAsync` lists only top-level boards, new `GetChildBoardsAsync`,
`CreateBoardAsync(parentId, sectionId)`, `DeleteBoardAsync` recurses the subtree (recycle), new
`MoveAssetWithinBoardAsync`. **Phase 2 (Desktop, done — needs runtime verification):** the Board screen shows its
child folders as **board cards** (identical to top-level boards — collage + pencil edit), **sorted before the
images in one scroll**, led by a **+ New folder** card; a folder card opens its own Board screen (drill-down to
any depth), and its pencil renames/deletes it (`BoardEditSheet` with `ShowSources=false`); the detail panel gains
**Move to folder…**. The masonry stays virtualized under the folder header (`MasonryLayout` realizes from the
effective viewport). A new `IResumable` nav hook reloads a board's folders + grid when revealed.
**Phase 3 (ingest side, done + tested):** `SourceMediaItem` gains `SectionId/Name/Url`; `ImportAsync` files a
sectioned pin into a child folder (`GetOrCreateSectionFolderAsync`, matched by `SourceSectionId`) while a loose
pin lands on the board; `RemoveSourceAsync` is now subtree-scoped (shared `CollectionTree.SubtreeIdsAsync`) so a
source's foldered pins go with it; `GetKnownItemsAsync` pre-skips held section pins on re-sync; a defensive
`PinterestSidecarParser` section probe. **Phase 0 (pending — user runs):** a gallery-dl spike to confirm the
exact per-pin section field/flag (the real archive shows only `board.section_count`); until then the connector
passes no section flag and auto-foldering is a graceful no-op. **Live-routing + card counts:** imported pins now
stream into the right place live (a sectioned pin updates its folder, never the root grid) via
`IngestProgress.ImportedIntoCollectionId`; board/folder **card counts roll up the whole subtree**; covers are
**spread (most-recent · midpoint · oldest)** across the subtree so the 3-up and "All images"/project collages
pull from a variety of boards. **An `xhigh` code-review pass** (15 findings) was then applied: the failed-import
empty-check is subtree-aware (no recycling of downloaded section pins), the move "already-there" branch detaches
attribution, `LibraryViewModel` is `IResumable` (cards refresh on return), re-attached orphans carry their
collection id, `LoadFoldersAsync` is exception-safe (build-then-swap), `OnResumed` reloads folders only (keeps
scroll/selection), `GetCoverAssetsAsync` seeks spread positions instead of loading all ids, `GetChildBoardsAsync`
scopes the rollup to the parent subtree, the section-folder cache is keyed by parent reference, `ParseSection`
falls through to the flat probe, `ImportStatus` resets the collection id symmetrically, a v7 column-definition
parity test was added, and shared `SpreadSelect`/`BoardCardCovers` helpers removed cover/spread duplication.
All uncommitted on top of `e8285d3`. Tests: **95 (Core) + 29 (Desktop)**, green; build clean. **Next:** Phase 0
spike → confirm/adjust the connector + parser; the Image-detail screen; retiring FluentTheme; deeper review
refactors. Material-shader background was dropped earlier (don't resurrect).

**Review follow-ups (deferred, not bugs):** (1) the **folder-edit block** in `BoardViewModel`
(`BeginEditFolder`/rename/clear-cache/delete, ~70 lines) still duplicates `LibraryViewModel`'s board-edit — both
operate on `BoardCardRef`; extract a shared `BoardCardEditor` rather than refactoring the MVVM blind. (2) The
**last-source** `RemoveSourceAsync` sweep deletes the board's whole subtree incl. user-filed (null-attribution)
folder pins — this is **intended** ("remove the board's last source = empty the board, no orphaned churn"); a
*multi-source* removal now spares user-moved pins (their attribution is detached on move). (3) `ReattachOrphans`
re-files a restored sectioned orphan onto the **root**, not its section (the sidecar carries no section id).

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
  tombstone columns, v3 = board merge (`Collection.DisplayName` + the `CollectionSources` table, with a
  backfill of legacy single-source boards), v4 = per-pin provenance (`CollectionItem.CollectionSourceId`,
  nullable FK ON DELETE SET NULL), v5 = data-only attribution backfill (`SourceAttributionBackfill` tags pre-v4
  pins to their source — single-source trivially, merged boards by the board id in each asset's sidecar), v6 =
  data-only repair (delete the stale source/board-removal **tombstones** a buggy earlier build left — they were
  acting as a re-import blacklist; matched by auto-note, leaving real per-image-delete tombstones). Older project
  DBs upgrade in place; fresh ones are built from the full model. A test asserts the v3 hand-written DDL matches
  what EF generates for `CollectionSources`. v5/v6 are C#/DML post-DDL steps gated `if (current < N)` after the
  DDL loop (each stamps its own version) — keep that ordering.
- **gallery-dl archive is a derived projection, not a second source of truth.** Before each import the
  connector rebuilds `download-archive.db` from what the library tracks (live + tombstoned, via
  `ConnectorOptions.KnownItems`), so it can never drift from the DB or silently hide an item.

**Design system / UI redesign (committed — `c43c06a`…`e8285d3`)**
- Own Avalonia styles, **no third-party UI library**; shadcn/ui reference + Lucide icons (embedded geometries).
  Full spec in **`DESIGN.md`** (now reconciled to the tokens). Clean/minimal, **dark-primary**, glossy soft-UI
  depth, indigo accent.
- **Tokens = single source of truth** (`Theme/Tokens.axaml`, light/dark `ThemeDictionaries`), layered over
  FluentTheme (opt-in via `Theme=`/class) so existing screens are untouched. Now include **control heights**
  (`ControlHeight` 38 / Sm 28 / Lg 48 — the explicit button-size metric), radii (incl. `RadiusLgTop`), spacing,
  type, gloss gradients, and **dark-tinted tight layered shadows** (kept tight to avoid 8-bit banding on the
  dark surfaces). Dark neutral ramp **retoned** (bg `#212025`, card `#1F1F23`, …).
- **Component kit** (ControlThemes under `Theme/Controls/`):
  - **Button** — variants default/secondary/outline/ghost/destructive; sizes `sm`/default/`lg` (explicit
    `Height`) + square `icon`; gloss fills, press scale+inset, locked inverse-luminance text-shadow;
    ghost/outline lift into the secondary pill on hover (outline keeps a rest border).
  - **Switch** (`HoardSwitch`, on `ToggleButton`) — real sliding-knob track switch (not a recoloured button).
  - **Input** (`HoardInput`) — recessed TextBox, working selection/caret, `:focus` primary tint.
  - **Row** (`HoardListItem`, on `ListBoxItem`) — tappable list row, native hover/press/selected; **replaces the
    old `Border.row` class**; used via a `ListBox` `ItemContainerTheme`.
  - **Card** (Border class) — raised surface; the project-board card has a 3-up **Pinterest collage cover**
    (most-recent / median / oldest) + metadata. **Badge** = floating chip. Text helpers (h1/h2/muted/accent).
  - **Pressable** (`PressableSurfaceTemplate`) — the **one shared press-surface template** (Border#root +
    hit-transparent content + press transition) that Button and Row reference, so the scaffold lives in one place.
  - `Icon` control + Lucide set (incl. sun/moon/pencil/check/external-link).
- **Cards + popups** (`Theme/Controls/` are styles; these are `Controls/*.{cs,axaml}` UserControls/controls):
  - **`ProjectCard`** — **concave inset** panel (recessed, not floating): `RecessedBrush` fill + `ShadowInsetBevel`
    (diagonal dark-top-left / light-bottom-right), no drop shadow; 3-up collage (also inset) + name + meta +
    **Open** (primary) / **Edit** (pencil) both `lg`.
  - **`BoardCard`** — **raised, clickable** (card body opens; scale-on-hover / shrink-on-press + `ShadowRaisedBevel`
    convex bevel, the buttons' tactile edge). Collage IS the card (full-bleed); name/meta + floating pencil **below**.
  - **`ProjectEditSheet` / `BoardEditSheet`** — Edit-popup contents (in a `SheetHost`): rename toggle (read-only ↔
    editable with ✓/✕), info rows, action group. BoardEditSheet has the **merge source-board list** (open / remove
    / add) — see decisions below.
  - **`ConfirmSheet`** — reusable confirm popup: title/message + Cancel/Confirm (`lg`, `FlexWrapPanel`) with an
    optional **N-second cooldown** (Confirm disabled, countdown a subscript under the centred label).
  - **`SheetHost`** (`Controls/SheetHost.cs` + `Theme/Controls/Sheet.axaml`) — in-app modal: scrim + responsive
    centred card (40px inset reserves `ShadowMd` room; content scrolls/shrinks, no fixed width).
  - **`ToastService` + `ToastHost`** — auto-dismiss toasts, bottom-right, `ClipToBounds=False` throughout so
    shadows aren't cropped.
  - **`FlexWrapPanel`** (`Controls/`) — flex-wrap-with-grow button group: items share the row equally, wrap one at
    a time when narrow. Used by every action button group.
- **One light source: top-left** — gloss gradients run diagonally; raised drops fall bottom-right, recessed/inset
  bevels are dark-top-left + light-bottom-right. New tokens: `ShadowInsetBevel`, `ShadowRaisedBevel`, `RecessedBrush`
  (darker-than-bg in dark so the inset reads sunken), `ScrimBrush`; light `LineBrush` bumped to ~18% to match dark.
- **Gallery** (`GalleryWindow`, `HOARD_GALLERY=1`) — live catalogue: full button matrix (every variant × size),
  switch, input, selectable rows (ListBox), **project/board cards + their Edit popups + ConfirmSheet**, sheet/toast
  demos, icons, swatches; header **sun─switch─moon** theme toggle.
- **`FocusManagement`** (`Infrastructure/`) — reusable click-away-clears-focus behaviour; pure decision
  unit-tested + an architecture test enforcing focus primitives stay centralised.
- **Shell is fluid reflow** (real DIPs; masonry grows columns by width). An earlier uniform-scale `DesignSurface`
  experiment was tried then **removed** (cropped content couldn't scroll). See DESIGN.md "Layout & responsive".
- **Convention (CLAUDE.md):** custom interactive control templates set content `IsHitTestVisible=False` so the
  fill Border is the single hover/hit surface (else `:pointerover`/`:pressed` flickers across the boundary).

**Phase 2 — screen integration (committed in `e8285d3`)**
- **Nav flow Projects → Library → Board** via `NavigationService` (now **disposes popped/reset pages** so
  per-board VMs + their subscriptions don't leak). `MainWindowViewModel` is the page factory and holds the
  per-project `ThumbnailCache` shared by Library covers + Board tiles.
- **Projects screen** (`ProjectLauncherView` + VM): inline cards replaced by the **`ProjectCard`** component;
  leading **`NewProjectCard`** "+ New project"; **`ProjectEditSheet`** Edit popup (rename = **folder rename on
  disk** via `ProjectManager.RenameProject`; lazy DB counts via the path-scoped **`ProjectStatsReader`**;
  recycle delete via **`ConfirmSheet`**). Card meta shows cache size.
- **Recycle-bin delete**: `IFileRecycler` (Core, platform-neutral) + `WindowsFileRecycler` (Desktop, P/Invoke
  `SHFileOperation` + `FOF_ALLOWUNDO`, registered in DI). `ProjectManager.DeleteProject` recycles when a
  recycler is supplied (else permanent — tests unaffected).
- **Library screen** (`LibraryViewModel`/`LibraryView` — the old single screen was **split**): board grid of
  **`BoardCard`s** (collage covers via `LibraryService.GetCoverAssetsAsync` + the thumbnail cache; meta = "N
  images · size") led by **`NewCard`** + an **"All images"** card. Top bar = back · project name (search bars
  removed for now; backend dormant). The pencil opens **`BoardEditSheet`** (rename via `CurationService.
  RenameBoardAsync`, counts/size/dates via `GetBoardDetailAsync`, the import source listed, clear-cache evicts
  the board's thumbnails, delete-board via `ConfirmSheet` removes the board **and its images**).
- **Board-first import**: the import sheet picks a **target board** (new — named from the URL — or existing to
  merge into); the board is **created up front** (`IngestService.CreateBoardAsync`) so it appears immediately,
  and `ImportAsync(targetCollectionId)` links every pin into it. A shared **`ImportStatus`** drives a **pinned
  inline progress strip** on the board card *and* the open Board screen (live count — no %, since gallery-dl
  reports no total — + pins streaming into an open board live). Accent-coloured progress bars.
- **Board screen** (`BoardViewModel`/`BoardView`): `ItemCard` masonry + the detail overlay + GIF autoplay (LRU)
  + delete (still the `DeleteDialog` window, for the note) / restore — the asset-grid logic extracted from the
  old `LibraryViewModel`.
- **Grid clipping fix** (recurring "card crops on hover"): the `ItemsControl`/`ItemsRepeater` item containers
  clip; fixed via `ClipToBounds=False` on the grid + panel + a `ContentPresenter` style + scroll inset. Rule
  written into CLAUDE.md.
- Cards are **flat at rest, drop shadow on hover** (`ShadowNone` → `ShadowRaised` → `ButtonRestShadow`, all from
  styles) — `BoardCard` / `NewCard` / `NewProjectCard` match the `ItemCard`. `NewProjectCard` sizes from an
  **invisible sizer** mirroring the `ProjectCard` content box, so heights match exactly (no magic number).

**Phase 2 — board-merge data model (committed in `e8285d3`)**
- A local board can now gather pins from **several Pinterest source boards** (a "merge"). Additive schema v3:
  - `Collection.DisplayName` — a **local rename override**; `Name` keeps the source/original name, so the shown
    title is `DisplayName ?? Name` and renames survive re-import. `CurationService.RenameBoardAsync` writes it.
  - `CollectionSource` table — the authoritative **one board ↔ many sources** list (board id / url / name /
    added-at). `IngestService` records the source(s) a board is filled from (target-import *and* auto-folder
    paths) idempotently; `SchemaInitializer` v3 creates the table and **backfills** existing single-source
    boards from their denormalised `Source*` columns.
  - Incremental re-import (`GetKnownItemsAsync`) now spans **every** merged source (flat join, not a
    SQLite-unsupported `SelectMany`/APPLY), so a re-crawl of any contributing board skips pins already held.
- `BoardEditSheet`'s **remove-source is real**, routed through a **`ConfirmSheet`**: `CurationService.RemoveSourceAsync(id)`
  drops the `CollectionSource` and **hard-deletes that source's own live images** — removes the asset rows
  (cascades links + tags; content-addressed = gone from every board), logs a Remove, and **recycles each blob to
  the OS bin** (`IFileRecycler`, fallback permanent). NOT a tombstone (no placeholder, not in-app restorable); a
  re-import re-fetches them fresh. Per-pin provenance (`CollectionItem.CollectionSourceId`, set at import; **v5
  backfills pre-v4 pins** so it works on existing data) identifies "its images"; when it's the board's **last**
  source, the remaining live pins are swept too. **`DeleteBoardAsync` is the same** (hard delete + recycle, then
  remove the grouping). Both skip already-tombstoned assets, so **per-image delete stays the restorable tombstone**
  (`DeleteAssetAsync`/`RestoreAsync`, unchanged). **Add-source** re-opens the import sheet targeted at the board.
  `LibraryService.GetBoardDetailAsync` returns the full `Sources` list with each source's removal count (the
  whole board for a sole source); the popup shows "Local board / Imported from Pinterest / Merged from N boards".
- **Sync** a board from its sources: a Board-screen top-bar button (shown for a real board with ≥1 URL'd source)
  opens a cookie sheet, then re-runs the import for each `CollectionSource` URL (`GetBoardSourceUrlsAsync` →
  `BoardViewModel.SyncAsync` → `ImportAsync(targetCollectionId)`). Pulls in missing/new items (interrupted
  import, source grew, items lost) and **skips already-held + tombstoned (blacklisted)** items — pure pipeline
  reuse; progress flows through the same `ImportStatus`.
- **Re-import/sync actually repopulates a board** (two import-correctness fixes): (1) the skip-archive is
  **target-aware** — `GetKnownItemsAsync(targetCollectionId)` pre-skips only pins already in *that* board (+ the
  global tombstone blacklist), not pins merely held in another board, so a missing live pin is downloaded +
  linked instead of skipped; (2) `ReattachOrphansAsync` re-links **orphaned live assets** (zero links) belonging
  to an imported board — matched by the board id in their stored sidecar (`SidecarBoardId.From`) — so a
  restored-but-uncrawled image (pin removed from the Pinterest board) returns to the board from existing content.
  Tombstoned orphans are never re-attached.
- Tests: `BoardMergeTests` (merge records both sources, re-import doesn't duplicate a source, rename = override,
  per-pin attribution, remove-source hard-deletes only that source's pins / the whole board when it's the only
  source / sweeps the last source's un-attributed remainder, blobs recycled via a fake `IFileRecycler`,
  board-delete hard-deletes + recycles, board source-URLs query, known-items span all sources, board detail,
  target-aware skip-archive excludes other-board pins / still blacklists tombstones, orphan re-attach by sidecar
  board / not for tombstoned orphans, url-less source becomes syncable after a real import) + schema-DDL-parity +
  v3/v5 backfills + **v6 stale-tombstone repair** + the upgrade on a copy of the real DB (574/574 pins attributed;
  v6 cleared 81 stale tombstones, kept 3 real deletes). **74 Core / 29 Desktop.**

**Phase 2 — compatibility & resilience (committed in `e8285d3`)**
- **Older project DBs**: already upgraded in place by `SchemaInitializer` on open (the v1→v5 chain).
- **Marker recovery**: `HoardProject.Open` tolerates a present-but-malformed marker (derives the name);
  `HoardProject.Adopt`/`ProjectManager.Adopt` rewrite a missing/corrupt marker. Offered only via the explicit
  **"open existing folder"** path with an Adopt confirm (so "open recent" stays strict); `ProjectManager` prunes
  recents whose folder vanished.
- **Missing/altered blobs**: a *live* asset whose blob was deleted/moved/altered outside the app is detected
  lazily per-tile (`File.Exists`, no bulk scan) and shows a **"file missing"** `ItemCard` state with a one-click
  **re-download** (`IngestService.RefetchAsync`, sharing `ReDownloadAsync` with restore). Cheap on open (schema +
  marker + recents only); full content-hash verification deferred to a future on-demand "verify project" action.
- Tests: marker corrupt-tolerance, adopt (recreate/refuse), manager adopt, recents pruning, refetch (no-op when
  present, throws with no URL). (Current suite total is in **Status** / **Uncommitted**.)

## Next up — finish Phase 2

- **Sub-boards / sections** — Pinterest sections are stored as child `Collection`s; the Board screen currently
  shows only loose pins. Show child boards as `BoardCard`s to drill into (the decided "keep sections as sub-boards").
- **Image-detail as a pushed screen** — currently a Board-screen overlay; convert to a back-stack screen, and
  migrate its **delete** off the `DeleteDialog` window (a note-collecting in-app sheet that routes through
  `ConfirmSheet`). Then **retire the old single-screen `LibraryView` remnants + FluentTheme + the placeholder
  `accent`/`danger`/`overlay` styles** (the new screens already use the token theme).
- **Restore project search** (removed from the top bars for now; backend still in the VMs) — place it wherever
  the redesign lands it.
- **Manual collections** (user-created, not just source boards) — slots into the redesigned Library screen.
- **FTS5** search — deferred scale-up from the current LIKE (additive aux table); premature while libraries
  are small (LIKE is fine for hundreds of pins), so low priority.
- ~~**Tag filtering**~~ — **deprioritised**: confirmed against real data (78 pins) that the Pinterest
  sidecars carry no `tags`/`hashtags` field, so a tag filter would be empty. Revisit only if a connector
  surfaces real tags.

### Review follow-ups (from the earlier **board-merge** xhigh review; still open. The **nested-folders** review's deferred items are in **Status** above.)

- **Pin-id-keyed link model (the deep one).** Today four mechanisms cooperate to get a pin into a board —
  crawl+link, dedup-by-SHA, the target-aware skip-archive, and orphan re-attach — because dedup keys on content
  hash while gallery-dl's archive (and the pin) key on pin id. Storing the **board id (and pin id) on the `Asset`
  at import** and deduping/linking by pin id would collapse skip + dedup + orphan-reattach into one rule and make
  the edge cases (SHA-change duplicates, restored-but-uncrawled orphans, the empty-board limitation) disappear.
  This also retires **`SidecarBoardId.From`**, which currently re-parses the Pinterest sidecar shape in Core and
  duplicates `PinterestSidecarParser` (drift risk: if the sidecar shape changes and only the connector is updated,
  orphan re-attach + the v5 backfill silently stop matching).
- **Orphan recovery has a hard floor:** a board that's *entirely* gone at the source (zero current pins) and has
  no local `CollectionSource` can't have its orphans re-attached — we can't learn its board id from an empty
  crawl. The pin-id-on-Asset model fixes this; until then, surface orphaned live assets in an "Unfiled" view so
  they're never invisible.
- **`RestoreAsync`/`RefetchAsync` reassign `Sha256`** — if a re-download ever yields bytes matching another
  asset, the unique `Sha256` index throws. Dedup/merge onto the existing asset instead of blindly assigning.
- **`SyncAsync` re-entrancy + `RefetchTile` is `async void`** — add a guard so two syncs/imports can't overlap on
  the shared `ImportStatus`, and give the refetch a `CancellationToken` (navigating away mid-refetch mutates a
  disposed VM).
- **`AssetTileViewModel.EnsureThumbnailAsync` does `File.Exists` inline** (on the realising thread) — move the
  missing-file probe into the existing off-thread decode path so it can't jank masonry scroll on a slow drive.
- **`GetBoardDetailAsync` runs the per-source count query even for single-source boards** where the result is
  discarded — skip it when `Sources.Count <= 1`.
- **Last-source sweep + manual collections:** `RemoveSourceAsync`'s last-source branch hard-deletes *every*
  remaining live pin; once manual (non-source) collections land, exclude manually-added images from the sweep.
- **Recycle path doesn't prune empty shard dirs** (the permanent-delete path does) — harmless empties accumulate;
  fold pruning into the recycle path (or route recycle through the store).
- **Denormalised `Collection.SourceBoardId`/`SourceUrl` "primary pointer"** is now write-mostly (only the
  auto-folder import path reads it); fold it into a computed `Sources.First()` or drop it to remove the
  two-sources-of-truth maintenance.

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
- `LibraryService.GetCollectionsAsync` emits a correlated COUNT + SUM(bytes) per collection (N+1 at large board
  counts), both excluding tombstones — fine for tens of boards; revisit if board counts get large. Board covers
  also run one `GetCoverAssetsAsync` query per card.
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
- No paging: `BoardViewModel` materialises a tile VM per asset (cheap via virtualisation, but the VM list
  is full) — page when boards get very large.
- Optional: add an `.editorconfig` to enforce the C# formatting conventions documented in `CLAUDE.md`.

## Product decisions & their current state

The settled decisions and how far each is implemented after the integration batch:
- **A board merges multiple Pinterest source boards** — *done (data model + un-merge + sync).* `CollectionSource`
  records the many sources; importing into an existing board adds a source; `BoardEditSheet` lists them with real
  add/remove, and the Board screen has a **Sync** button. Remove-source confirms via `ConfirmSheet` and
  **hard-deletes that source's images to the recycle bin** (per-pin provenance, v4/v5; *not* a tombstone — a
  re-import re-fetches them); removing the last source sweeps any un-attributed remainder. Not yet: merged sources
  don't show as drill-in sub-boards (the item below).
- **Rename = local display-name override** — *done for boards* (`Collection.DisplayName`, non-destructive,
  survives re-import). Project rename still **renames the folder on disk** (a project *is* its folder).
- **Delete → OS recycle bin** — *done* via `IFileRecycler`/`WindowsFileRecycler`. **Project delete** recycles the
  folder. **Remove-source** and **board delete** now **hard-delete** the affected images (rows + links gone
  everywhere) and **recycle their blob files** to the OS bin (re-import re-fetches them). **Per-image delete** is
  the lone exception — it tombstones (in-app restorable; blob freed permanently), by design.
- **Confirm everything destructive via `ConfirmSheet`** — *done* for project delete (10s cooldown) and board
  delete (5s). Per-image delete still uses the `DeleteDialog` **window** (it collects the tombstone note) —
  migrate to a note-collecting in-app sheet at the Image-detail-screen step.
- **`ProjectLauncherView` inline cards → `ProjectCard`** — *done.*

## Committed this phase

- `4f62109` design-system kit + gallery; dropped the material-shader background (`Rendering/`, `Assets/Materials/`
  **gone** — don't resurrect).
- `10b8106` card designs · `b64514a` UI improvements — the nav back-stack, the first Projects-screen redesign
  (`ProjectLauncherView` collage cards + new-project sheet + toasts), the `ProjectCard`/`BoardCard` components,
  `ProjectEditSheet`, `SheetHost`/`ToastHost`/`FlexWrapPanel`, the top-left-light-source token pass.
- `116b91e` **`ItemCard`, `ConfirmSheet`, `BoardEditSheet` as gallery components** (+ icons, gallery wiring).

## Uncommitted (working tree) — the nested-folders batch (on top of `e8285d3`)

Everything in **Status** above (nested folders Phase 1–3 + live-routing/counts/covers + the `xhigh` code-review
fixes). **New files:** `Core/Metadata/CollectionTree.cs` (subtree BFS, shared by delete/remove-source/known-items/
counts/covers), `Core/Library/SpreadSelect.cs` (even-spread index math), `Desktop/ViewModels/BoardCardCovers.cs`
(shared cover loader), `tests/Hoard.Core.Tests/NestedBoardTests.cs`, `tests/Hoard.Core.Tests/PinterestSectionParseTests.cs`.
**Modified — Core:** `Domain/Collection` (+`SourceSectionId`), `Connectors/ISourceConnector` (+`Section*` on
`SourceMediaItem`), `Ingest/IngestProgress` (+`ImportedIntoCollectionId`), `Ingest/IngestService` (section folders,
known-items subtree, reattach id), `Library/CurationService` (subtree delete/remove-source, move), `Library/LibraryService`
(subtree counts + scoped rollup, spread covers, child boards, subtree shas/detail), `Metadata/SchemaInitializer`
(v7 guarded add). **Ingest:** `PinterestSidecarParser` (`ParseSection`). **Desktop:** `BoardViewModel` (folders,
move, live routing, debounce dispose, OnResumed folders-only), `LibraryViewModel` (`IResumable`, edit-button +
auto-discard fixes, live routing), `MainWindowViewModel`, `ProjectLauncherViewModel` (spread thumbnails),
`ImportStatus` (+`LastImportedCollectionId`), `Navigation/NavigationService` (`IResumable`),
`Views/BoardView.{axaml,axaml.cs}` (folder grid, sheets, move), `Controls/BoardEditSheet.{cs,axaml}`
(`ShowSources`/`Heading`/`DeleteLabel`). + `SchemaInitializerTests`, docs. **Tests: 95 (Core) / 29 (Desktop) green;
build clean** (the running app locks the Desktop DLLs — close it to build/run the Desktop suite).

**Before commit — runtime-verify** (close the app first): import a board → loose pins stream onto the grid while
sectioned pins file into their folder cards live; drill into a folder (any depth) + back keeps scroll/selection;
create/rename/delete a folder via its pencil; move a pin to a folder from the detail panel; board/folder card
counts include section images; "All images" + project covers pull from varied boards; a failed import discards
only a *truly* empty new board (an all-sectioned partial survives). **Phase 0 spike still pending** (gallery-dl
section field) before auto-foldering does anything against real Pinterest.
