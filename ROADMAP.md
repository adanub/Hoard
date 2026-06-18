# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-06-17):** Phase 1 complete. Phase 2 — UI/UX redesign. The design-system **component kit + a full
card/popup set** are built and live in the dev gallery (`HOARD_GALLERY=1`): **ProjectCard** (concave inset),
**BoardCard** (raised, clickable, scale feedback), their **Edit popups** (`ProjectEditSheet` / `BoardEditSheet`,
the latter with the merge source-list), the cooldown **`ConfirmSheet`**, plus **`SheetHost`** (modal),
**`ToastHost`**, **`FlexWrapPanel`**, the nav **`NavigationService`**, and a **top-left-light-source** token pass
(diagonal gloss + bevels). Material-shader background was dropped earlier (don't resurrect). Mostly committed
(`10b8106` card designs, `b64514a` UI improvements); **uncommitted:** `BoardEditSheet`, `ConfirmSheet`, their
gallery wiring, new icons, **`ItemCard`** (masonry media tile). **These are GALLERY components — not yet wired
into the real Projects/Library screens.** Tests: 47 (Core) + 29 (Desktop), green. Next: **integrate the cards
into the screens** (see "Decided, pending integration").

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

## Next up — finish Phase 2

- **Finish the redesign (in progress).** The component kit + the full card/popup set (incl. the **ItemCard**
  media tile) are built **as gallery components**. Remaining: **integrate the cards into the real screens** — the
  Projects screen (grid of `ProjectCard`s), the Library/Board screens (grids of `BoardCard`s + `ItemCard` masonry
  tiles, the Edit popups
  wired to real data + the merge/recycle/rename backends below), the nav back-stack (Projects → Library → Board
  → Image-detail), then **retire FluentTheme + the placeholder `accent`/`danger`/`overlay` styles**. The cards
  currently carry pre-formatted display strings + `ICommand`s (host supplies real data/commands at integration).
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

## Decided, pending integration (the card/popup backends)

The cards/popups are built as **gallery components with placeholder data** — these product decisions are
settled and must be honoured when wiring them into the real screens:
- **A board merges multiple Pinterest source boards.** A local "board" holds a **list of source-board refs**, so
  several Pinterest boards can be composed into one. The `BoardEditSheet` is where you manage that list (open /
  remove / **add** sources). Needs a data-model change (board ↔ many `SourceBoardRef`).
- **Rename = local display-name override.** Renaming a board/project sets a local name; the original Pinterest
  source name is kept underneath so a future import won't clobber the rename.
- **Delete → OS recycle bin, not permanent.** Deleting a board/project hard-removes it **but moves the files to
  the platform recycle bin** (cross-platform "move to trash" — not yet implemented).
- **Confirm everything destructive** via `ConfirmSheet` (cooldown on board/project delete; no cooldown on
  remove-source). The old per-image delete still uses the `ConfirmDialog` **window** — migrate it to
  `ConfirmSheet` for consistency at integration.
- **`ProjectLauncherView` already has an *older* inline collage-card grid** (from the first Projects-screen pass:
  cards with a ⋯ menu, the new-project `SheetHost`, toasts — committed). At integration, **replace its inline
  cards with the newer `ProjectCard` component** (the inset design supersedes it).

## Committed this phase

- `4f62109` design-system kit + gallery; dropped the material-shader background (`Rendering/`, `Assets/Materials/`
  **gone** — don't resurrect).
- `10b8106` card designs · `b64514a` UI improvements — the nav back-stack, the first Projects-screen redesign
  (`ProjectLauncherView` collage cards + new-project sheet + toasts), the `ProjectCard`/`BoardCard` components,
  `ProjectEditSheet`, `SheetHost`/`ToastHost`/`FlexWrapPanel`, the top-left-light-source token pass, and the
  related doc updates.

## Uncommitted (working tree)

- New: `Controls/BoardEditSheet.{cs,axaml}` (merge source-list), `Controls/ConfirmSheet.{cs,axaml}` (cooldown
  confirm), `Controls/ItemCard.{cs,axaml}` (masonry media tile — convex bevel, hover-lift, GIF/VIDEO tag,
  memory badge, Unload, tombstone).
- Modified: `Icons.axaml` (+`external-link`), `GalleryWindow.{axaml,axaml.cs}` (board Edit popup + confirm
  wiring + ItemCard demo row; all destructive actions route through `ConfirmSheet`), `DESIGN.md` (ItemCard
  inventory entry).
- Verify in the gallery (`HOARD_GALLERY=1`, both themes) then commit. Build + 47 (Core) / 29 (Desktop) tests green.
