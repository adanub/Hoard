# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-17):** Phase 1 complete. Phase 2 in progress — UI/UX **redesign underway**: the design-system
component kit is **built, dialled in, and committed** (`4f62109`; live gallery catalogue). A material-shader
background was built then **dropped** along the way (don't resurrect it). **Slice 2 in progress** — the nav
back-stack (`NavigationService`) + a **redesigned Projects screen** (collage-card grid, "+ New" tile,
new-project **sheet**, **toasts**) are done but **uncommitted** (await runtime sign-off). Tests: 47 (Core) +
29 (Desktop), green. Next in slice 2: rebuild `LibraryView` on the kit, add the deeper Library→Board→Image
back-stack, then retire FluentTheme + the placeholder `accent`/`danger`/`overlay` styles.

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
  - `Icon` control + Lucide set (incl. sun/moon).
- **Gallery** (`GalleryWindow`, `HOARD_GALLERY=1`) — live catalogue: full button matrix (every variant × size),
  switch, input, selectable rows (ListBox), card-with-collage, icons, swatches; header **sun─switch─moon** theme
  toggle.
- **`FocusManagement`** (`Infrastructure/`) — reusable click-away-clears-focus behaviour; pure decision
  unit-tested + an architecture test enforcing focus primitives stay centralised.
- **Shell is fluid reflow** (real DIPs; masonry grows columns by width). An earlier uniform-scale `DesignSurface`
  experiment was tried then **removed** (cropped content couldn't scroll). See DESIGN.md "Layout & responsive".
- **Convention (CLAUDE.md):** custom interactive control templates set content `IsHitTestVisible=False` so the
  fill Border is the single hover/hit surface (else `:pointerover`/`:pressed` flickers across the boundary).

## Next up — finish Phase 2

- **Finish the redesign (in progress).** Slice 1 (component kit + gallery) is built + committed. Slice 2 is
  underway: the **navigation back-stack** (`Navigation/NavigationService.cs` — `Reset`/`Push`/`Pop`/`CanGoBack`,
  shell binds `Navigation.Current`) and the **Projects launcher rebuilt on the kit** are done (uncommitted).
  Remaining slice-2 work: **rebuild `LibraryView` on the kit**, wire a visible back affordance for the deeper
  Board → Image-detail pushes, then **retire FluentTheme + the placeholder `accent`/`danger`/`overlay` styles**.
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

## Recently landed (committed `4f62109`)

The design-system component kit + gallery (and the drop of the material-shader background) shipped in
`4f62109 feat: ui design components`, after the user verified the gallery at runtime. The kit is the
foundation slice 2 builds on:
- **Dropped the material/shader background feature** (minor polish): `Rendering/`, `Assets/Materials/`,
  `ASSETS.md`, and its gallery showcase are **gone** — don't resurrect it.
- **Component kit:** `Theme/Controls/{Button,Input,Switch,ListItem,Pressable}.axaml` + `Surfaces.axaml`
  (Row → `ListBoxItem`, floating Badge, Card), `Tokens.axaml` (dark retone, control-height/radius tokens,
  tight layered shadows), `Icons.axaml` (incl. sun/moon), `GalleryWindow.*` catalogue, `MainWindow.axaml`
  (fluid shell), `FocusManagement.*`.

## Working tree (uncommitted — await runtime sign-off)

Slice-2 work, on top of committed `4f62109`:
- **Nav back-stack:** new `Navigation/NavigationService.cs` (`Reset`/`Push`/`Pop`/`CanGoBack`, `ObservableObject`);
  `MainWindowViewModel` now owns it as the page factory (launcher = `Reset` root, library = `Push`;
  switch-project resets to a fresh launcher so recents reload) instead of the old swap callbacks;
  `MainWindow.axaml` binds `Navigation.Current`. New `NavigationServiceTests` (+6).
- **Projects screen redesigned** (per DESIGN.md "one job per screen" — replaced the old two-column
  recent-list + new-project form, *not* a restyle): `ProjectLauncherView.axaml` is now a reflowing grid of
  **project-board collage cards** led by a "+ New project" tile. Each card shows a 3-up collage built from
  **3 thumbnails read off the project's `thumbnails/` folder** (no DB opened; missing tiles stay muted) +
  name + cache size, with a **⋯ menu** (Open / Clear cache / Remove / Delete — delete keeps the 10s
  `ConfirmDialog`); the card body opens the project. `ProjectLauncherViewModel` rewritten accordingly
  (`Tiles` = `NewProjectTile` marker + `RecentProjectRef` cards; per-card actions; collage loading).
- **New components (reusable):** `Controls/SheetHost.cs` + `Theme/Controls/Sheet.axaml` — in-app modal
  **sheet** (scrim + floating card, Esc/scrim dismiss); `Services/ToastService.cs` + `Controls/ToastHost.*`
  — auto-dismissing **toasts** (shell-hosted in `MainWindow`, owned by `MainWindowViewModel`, passed to the
  launcher VM). New-project + "Open existing folder…" live in the sheet; all per-project feedback is toasted.
  New token `ScrimBrush`. Both added to the **gallery** (`GalleryWindow`) with live demos.
Commit after the user verifies (suggested split: nav spine · sheet+toast components · Projects redesign).

**Needs runtime verification (can't launch the GUI in-session):**
- **Projects screen (both themes):** the "+ New" tile + project cards reflow as the window widens; each card's
  3-up collage shows real cached thumbnails (muted tiles where a project has <3); card body opens the project;
  the ⋯ menu's Open/Clear cache/Remove/Delete all act on the right card (Delete still shows the 10s confirm).
- **New-project sheet:** "+ New" opens it; scrim/Esc/Cancel dismiss; name validation + path preview; Create
  opens the new project; "Open existing folder…" adopts a folder; errors show inline in the sheet.
- **Toasts:** clear-cache / remove / delete / open-failure show a toast bottom-right that auto-dismisses;
  errors read in the destructive colour. Gallery (`HOARD_GALLERY=1`) has Sheet + Toast demo sections.
- **Navigation:** open/create → library; **Switch project** returns to a fresh launcher with up-to-date recents.
Diagnose via `hoard.log`.
