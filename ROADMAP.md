# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-19):** Phase 1 complete. Phase 2 — UI/UX redesign, now **integrated into the real screens**
(the gallery components are wired up, not just demoed). The **Projects → Library → Board** nav flow is built and
runs: Projects grid (`ProjectCard` + `NewProjectCard`, Edit popup, recycle-bin delete, folder rename), Library
grid (`BoardCard`s + `NewCard` "+ New board" + "All images", board-first import with inline per-board progress,
`BoardEditSheet` for rename/sources/delete), and the Board screen (`ItemCard` masonry + detail overlay + GIF
playback + delete/restore). All this is a **large uncommitted batch** on top of `116b91e` (which committed the
gallery components). Tests: 47 (Core) + 29 (Desktop), green; build clean. **Next:** the board-merge data model
(additive schema — see "Still to do"), then sub-boards/sections, the Image-detail screen, and retiring
FluentTheme. Material-shader background was dropped earlier (don't resurrect).

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

**Design system / UI redesign (slice 1 committed; large refinement batch uncommitted)**
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

**Phase 2 — screen integration (uncommitted batch on top of `116b91e`)**
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
  the board's thumbnails, delete-board via `ConfirmSheet` keeps the images under All images).
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

## Next up — finish Phase 2

- **Board-merge data model (next).** The screens are wired, but a board is still a single `Collection`. To
  deliver the agreed **multi-source merge** + **local rename override**, add (additively, per the schema rules):
  `Collection.DisplayName` (nullable local override; falls back to the source `Name`) and a `CollectionSource`
  child table (one board ↔ many Pinterest source refs). Bump `SchemaInitializer.LatestSchemaVersion` with idempotent
  DDL matching EF's `GenerateCreateScript`. Then the `BoardEditSheet`'s **add/remove source** become real
  (currently: add re-opens the import sheet targeted at the board; **remove just toasts "coming with merge"**),
  and rename writes `DisplayName` instead of `Collection.Name`.
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
- **A board merges multiple Pinterest source boards** — *partial.* A board is still a single `Collection`;
  importing into an existing board (the "merge" path) works and the source is listed in `BoardEditSheet`, but
  the **many-source data model is not built** (see "board-merge data model" in Next up). Remove-source is a
  placeholder toast until then.
- **Rename = local display-name override** — *partial.* Board rename currently writes `Collection.Name` and
  project rename **renames the folder on disk**; the non-destructive local-override comes with the merge model.
- **Delete → OS recycle bin** — *done for projects* (`IFileRecycler`/`WindowsFileRecycler`). **Board delete**
  removes only the grouping (images stay under All images — full board+content recycle is deferred). **Per-image
  delete** still tombstones (frees the blob; not the recycle bin) — unchanged.
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

## Uncommitted (working tree) — the screen-integration batch

The whole "Phase 2 — screen integration" Done block above. New files: `Core/Library/ProjectStatsReader.cs`,
`Core/Storage/IFileRecycler.cs`, `Desktop/Services/WindowsFileRecycler.cs`, `Desktop/ViewModels/BoardViewModel.cs`,
`Desktop/ViewModels/ImportStatus.cs`, `Desktop/Views/BoardView.{axaml,axaml.cs}`, `Desktop/Controls/NewCard.{cs,axaml}`,
`Desktop/Controls/NewProjectCard.{cs,axaml}`. Modified across Core (`IngestService`, `LibraryService`,
`CurationService`, `ProjectManager`, `HoardProject`) and Desktop (`LibraryViewModel`, `MainWindowViewModel`,
`ProjectLauncherViewModel`, `AssetTileViewModel`, `NavigationService`, `App.axaml.cs`, `BoardCard`, `Surfaces.axaml`,
the `Library`/`ProjectLauncher` views, gallery) + docs. Build + 47 (Core) / 29 (Desktop) tests green.
**Runtime-verify the flow** (import → progress, board edit, recycle delete, masonry/GIF) then commit.
