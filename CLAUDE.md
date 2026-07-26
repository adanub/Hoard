# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Hoard is a self-hosted, local-first media archiver (.NET 10 + Avalonia). It backs up media + metadata
from the web into a project-based, content-addressed local archive; Pinterest (via gallery-dl) is the
first connector. See `README.md` for the user-facing model, project-folder layout, and the GIF/video
behaviour. The long-term aim is cross-platform (desktop now; mobile + a sync server later) — that goal
drives the assembly boundaries below, so respect it.

## Commands

```bash
dotnet build Hoard.slnx                                   # build everything (solution is .slnx, not .sln)
dotnet run  --project src/Hoard.Desktop                   # run the app (launches from a terminal so logs stream)
dotnet test Hoard.slnx                                    # all tests (Hoard.Core.Tests + Hoard.Desktop.Tests)
dotnet test tests/Hoard.Core.Tests/Hoard.Core.Tests.csproj          # one project
dotnet test Hoard.slnx --filter "FullyQualifiedName~Search_filters" # one test / class
pwsh tools/fetch-gallery-dl.ps1                           # download the bundled gallery-dl binary for this OS (not committed)
```

- **The running app locks its output DLLs**, so a full build fails with `MSB3027` while it's open. Build a
  non-Desktop project (`tests/...` or `src/Hoard.Core`) to iterate, or close the app first.
- **`tools/gallery-dl/gallery-dl.exe` is required at runtime but gitignored** — run the fetch script after a
  clone. The Desktop csproj copies it next to the app output.

## CI & releases

- **CI** (`.github/workflows/ci.yml`) builds + runs the full test suite on every PR and push to `main`
  (ubuntu), so tests must stay platform-neutral — validate against fixed cross-platform rules (e.g.
  `HoardProject.ValidateName` applies the Windows-invalid filename set on every OS), never
  `Path.GetInvalidFileNameChars()`-style current-OS behaviour.
- **Releases are automated from Conventional Commits** (`.github/workflows/release.yml`): release-please keeps
  a release PR open on `main` with the next semver computed from commit types since the last release (feat →
  minor, fix/perf → patch, `feat!`/`BREAKING CHANGE` → major; pre-1.0 the bumps are shifted down one level) plus
  the `CHANGELOG.md` update. **Merging that PR** creates the `vX.Y.Z` tag + GitHub Release, and the build matrix
  uploads self-contained apps (win-x64 zip; osx-arm64/osx-x64 ad-hoc-signed `.app` zips, template at
  `tools/packaging/macos/Info.plist`) with SHA-256 checksums and build-provenance attestations. The version is
  stamped at publish time via `-p:Version` — **never hardcode a version in a csproj**, and never hand-edit the
  release-please-owned files (`version.txt`, `.release-please-manifest.json`, `CHANGELOG.md`). To force a
  specific version (e.g. the jump to 1.0.0), land a commit whose footer says `Release-As: 1.0.0`. The
  changelog starts fresh at pipeline adoption — `bootstrap-sha` in the config excludes all earlier history
  (it's only consulted until the first release; harmless after that).
- The build jobs chain off the release-please job via `needs` because events created with the workflow's own
  `GITHUB_TOKEN` never trigger other workflows; for the same reason the release PR gets no CI checks unless a
  PAT is configured. `workflow_dispatch` on the release workflow is a packaging dry run (artifacts only, no
  release). All actions are pinned to full commit SHAs; Dependabot (`.github/dependabot.yml`) refreshes the pins
  weekly — keep new actions pinned the same way.

## Architecture

Three assemblies, split by *platform reach* — this is the load-bearing decision, keep it intact:

- **`Hoard.Core`** — platform-neutral. Domain, EF Core/SQLite metadata, the SHA-256 content-addressed blob
  store, the project model, ingest + library services, and the `ISourceConnector` abstraction. **Must stay
  free of subprocess / P-Invoke / Avalonia code** so a future mobile head and sync server can reference it.
- **`Hoard.Ingest.GalleryDl`** — desktop/server only. The gallery-dl connector spawns a subprocess (forbidden
  on iOS / restricted on Android), so it lives here, not in Core. Browser-cookie detection lives here too.
- **`Hoard.Desktop`** — Avalonia MVVM app (views, view models, custom controls). Hosts Core in-process for now.

Concepts that span multiple files:

- **Storage is project-scoped, and the archive format is v2 — "immutable archive + derived index"
  (`SYNC-DESIGN.md` is the deep doc; read it before touching storage/sync).** A "project" is a user-chosen
  folder (`HoardProject`) whose durable contents are **static only**: the marker (`hoard.project.json`,
  carrying a stable `ProjectId` + the archive `format`), the content-addressed `store/`, and the
  **append-only per-device op segments** `ops/<deviceId>.jsonl` (rotating into closed, never-renamed
  chapters `<deviceId>.00001.jsonl`… at a size threshold) — the replayable history of every change
  (`Sync/` — `ArchiveLog` emits ops in the same SaveChanges as each change; `ArchiveSync` catches up other
  devices' segments at open; `ArchiveSegments` owns the torn-tail-safe file format). The **metadata DB is a
  per-machine derived index** at `%APPDATA%/Hoard/projects/<projectId>/index.db` — rebuildable from the
  segments alone (delete it and the next open replays it), which is why SQLite never touches a network
  share and a NAS-hosted project opens from any machine. Legacy (format v1) projects keep `hoard.db` in the
  folder until the user accepts the one-time migration (`Sync/ArchiveMigration`, offered by the launcher;
  keeps a `hoard.db.pre-v2.bak`). `ProjectManager.Current` is the single source of truth;
  `ProjectDbContextFactory` and `ProjectMediaStore` read it, so opening/switching a project re-points all
  storage with no other wiring. SQLite uses **WAL** and **`Pooling=False`**. **All derived data is
  per-machine (P4):** `thumbnails/`, `logs/`, and `download-archive.db` live beside the index under
  `%APPDATA%/Hoard/projects/<projectId>/` — resolve them through `ProjectManager`'s
  `ThumbnailsRootFor`/`LogsRootFor`/`DownloadArchivePathFor` (format-aware: a legacy v1 project keeps its
  in-folder layout until migrated), never `HoardProject`'s raw paths; the open-time tidy
  (`ArchiveMigration.TidyMigratedFolder`) sweeps/relocates what older builds left in a v2 folder. **Never
  mutate or delete ops** — the log is append-only; blob writes stay temp-file + rename (`PutAsync` stages
  `.tmp-` + atomic rename, and **length-verifies a resident blob** — a crash-torn short file at a content
  address is replaced, never trusted).
- **Backup/replication (SYNC-DESIGN P5 R0–R2): the archive replicates to a remote by file-set
  reconciliation — no protocol.** The Library ＋ menu's **Backup** sheet (`Controls/RemoteSheet` +
  the remote region in `LibraryViewModel`) configures ONE per-machine remote per project
  (`Sync/RemoteConfig` at `appData/projects/<id>/remote.json`) and runs `Sync/RemoteSync.SyncAsync` =
  pull → apply-to-index (the same catch-up an open runs, so the grid refreshes immediately) → push.
  `Sync/ArchiveReplicator` moves only the archive proper (marker + `store/` + `ops/`) against a dumb
  `Sync/IRemoteStore` (`FileSystemRemoteStore` over any mounted path — the only implementation; the
  once-planned S3/B2 remote (R3) and fetch-on-demand replicas (R4) were dropped as unnecessary). The
  load-bearing rules: **blobs before segments both ways** (op-implies-blob must hold on the receiving
  side mid-crash); **chapters converge by length** with a just-before-upload re-stat
  (`GetLengthAsync` — a start-of-run snapshot lets concurrent pushers regress a chapter); **a pull never
  replaces this device's own EXISTING chapters** (the local writer is authoritative — a longer remote
  copy can be a stale pushed torn-tail; it still bootstraps a wiped folder) and `ArchiveLog.
  InvalidateFlushWatermark()` runs after any pull (a stale cached watermark would bake duplicate ops into
  the append-only segment); the remote marker must carry the **same ProjectId** before a byte moves
  (empty remote seeded on push, refused on pull); `.tmp-` staging names are **invisible to listings**;
  pull never deletes local state (removals travel as ops). **The import interlock is two-way via
  `ImportStatus.IsRemoteSyncing`** — imports/board-syncs refuse while a backup sync runs and vice versa
  (the replicator copies the very files an import writes); keep any NEW archive-writing entry point
  gated on both flags. Two folder copies with the same marker id are **replicas of one archive** (they
  share this machine's index); syncing them against each other is the intended workflow, not a conflict.
- **Human-readable export (the Board ＋ menu's Export).** `Library/BoardExporter` + the pure `ExportNames`
  (Core) materialise a board's subtree as a browsable `Board/Folder/image` tree at a user-chosen folder
  (`Controls/ExportSheet` + the export region in `BoardViewModel`; the folder picker lives in `BoardView`
  code-behind, like Backup's). **Read-only against the archive** — the `store/` stays content-addressed.
  File names are **stable per asset** (`Title [pinId].ext`, sha stub when pinless; the Windows-invalid
  char set applied on every OS; folder names honour `DisplayName` with `[id]` sibling-collision suffixes),
  which is what makes re-export an **incremental refresh**: a same-length destination file is skipped
  (blobs are immutable) and a torn one re-copies; writes are temp+rename. Tombstones never export; a
  missing blob is counted in the `ExportReport`, not fatal. Export **refuses to start** while an
  import/backup sync runs (a mid-run export would snapshot a partial board) but publishes no interlock
  flag of its own — nothing needs to gate on a reader.
- **Schema versioning is additive, via `PRAGMA user_version` — not EF migrations.** `EnsureCreated` builds a
  fresh project DB from the full current model; an existing DB (maybe from an older app version) is patched by
  `Metadata/SchemaInitializer.cs`, which applies the additive DDL upgrades it predates and stamps `user_version`.
  Each user owns many independent project DBs, so this is lighter than migrations. **When you change the model:
  bump `SchemaInitializer.LatestSchemaVersion` and add a matching idempotent `CREATE … IF NOT EXISTS` upgrade
  whose DDL matches what EF would generate** (verify with `db.Database.GenerateCreateScript()`). Non-additive
  changes (renames/drops) would need a real migration — cross that bridge if it ever comes up.
- **Ingest is a stream, not a batch.** `ISourceConnector.DownloadAsync` takes an `onItem` callback and invokes
  it per item as it lands; `IngestService` stores + upserts per item and reports the new `AssetView` via
  `IngestProgress.ImportedAsset`, which flows through `ImportStatus` so an open Board screen appends it live.
  Dedup happens twice: gallery-dl's `--download-archive` skips already-fetched pins *before* download, and the
  content-addressed store dedups by hash *after*.
- **The sync log is append-only and content-keyed.** Every add (`IngestService`, on a genuinely new asset)
  and remove (`CurationService.DeleteAssetAsync`) writes a `SyncOp` via `Sync/SyncLog.cs`, in the *same*
  `SaveChanges` as the change so the history can't drift. Ops are keyed by the asset's SHA-256, not its local
  row id, so they replay on another device that holds the same content under a different id. This is the
  Phase 3 (cloud sync) foundation; nothing reads it yet — keep it append-only (never mutate/delete ops).
- **Shell navigation.** `MainWindowViewModel` owns a `NavigationService` (`Navigation/`) — a browser-style page
  back/forward stack of *steps* (`Reset`/`Push`/`PushState`/`Back`/`Forward`/`CanGoBack`/`CanGoForward`) — and is the page factory:
  `ShowLauncher` (`Reset` root) → `ShowLibrary` (`Push`, board grid) → `ShowBoard` (`Push`, one board's masonry).
  The shell binds `Navigation.Current`; the template `ViewLocator` maps each page VM to its `…View` by name.
  **`Pop`/`Reset` dispose any `IDisposable` page** so a popped `BoardViewModel` releases its subscriptions (don't
  leak the per-board tiles). **The whole Projects → Library → Board → … chain is one back/forward stack:** `Reset`
  builds the root launcher **only at startup**; everything else is `Push`/`Pop`, so the Library's back **pops to
  the launcher** (which is `IResumable` and reloads its recents on reveal) rather than resetting — that's what
  lets forward re-enter a project. **Forward history keeps only a lightweight rebuild *thunk*, never a live
  page** — `Push` takes an optional `Func<ViewModelBase?> recreate` (`ShowLibrary` + `ShowBoard` pass one; the
  launcher is the root, never popped-to-forward); `Pop` disposes the page but stashes its thunk, and `GoForward`
  rebuilds a *fresh* copy (so a backed-out board/library still frees its thumbnails; forward re-loads it — its
  ctor reloads, so no `OnResumed`). A new `Push` clears the forward history, like a browser. A forward thunk
  always rebuilds for the **currently-open project** because the only way to change `ProjectManager.Current`
  (opening a project) goes through `Push`, which clears forward; a thunk **returns null when its project was
  deleted** while backed out (`_projects.Current is null`) so `Forward` drops the dead entry instead of building
  a blank page. **The history holds two kinds of step:** a `PageStep` (a page VM + rebuild thunk — Projects →
  Library → Board → …) and a `StateStep` (an in-page overlay on the *current* page — the Board's image-detail
  **band**, and the fullscreen **zoom**), applied/reverted against `Current` so forward re-opens it on a *rebuilt*
  page. `Back` reverts the topmost step (state → its `Revert`; page → **dispose then reveal** — dispose runs while
  the page is still `Current`/attached, a memory-load-bearing ordering pinned by a test), `Forward` re-applies it;
  `PushState`/`ReplaceTopState`/`DropCurrentStates` manage the in-page ones (the Board pushes them when the band/zoom
  open). **A state's `Apply` returns bool** — whether it took effect (or validly deferred until the page loads);
  `PushState`/`Forward` record nothing on failure, so a step whose target vanished (asset deleted while backed out)
  is dropped instead of becoming a ghost that eats a Back press. **Both `Back` AND `Forward` consult `IAbsorbsBack`**
  (swallowed while the board animates its band closed — a Forward mid-collapse would be a setter-equality no-op that
  desyncs history). **`DropCurrentStates` drops only the TOP run of forward `StateStep`s** — those provably belong to
  the current page (states beneath a page thunk belong to that rebuildable page) — so an in-page reload never kills
  the forward button. Board async loads are seq/dispose-guarded and only purge history when `_nav.Current == this`
  (a buried board's import-end reload must not pop the current page's open band). **Mouse back/forward (XButton1/XButton2) AND Esc** are handled on `MainWindow` as one
  unified "back": dismiss the topmost open `SheetHost` (a transient modal, never a history step — `OfType<SheetHost>()`)
  if there is one, else `NavigateBack` → `NavigationService.Back` (step zoom → band → page). Esc is a window-level
  **bubble `KeyDown`** (deliberately NOT tunnel, and without `handledEventsToo`): a focused control that wants Esc —
  an open ComboBox dropdown, a flyout, IME composition — gets it first and closes its own popup; only an unclaimed
  Esc reaches the window. The trade: this works because **no page control handles Esc while "closed"** (Avalonia's
  ComboBox/TextBox don't) — keep that invariant when adding controls, or Esc-as-back silently dies wherever such a
  control has focus. `SheetHost` itself must NOT handle Esc (it once did: with a confirm floating over an edit sheet,
  focus bubbling from the edit sheet's own button dismissed the *underlying* sheet); the window's topmost-open-sheet
  sweep is the single dismiss path. The floating bar's ← button and the breadcrumb's ancestor crumbs funnel
  into the same `Back` (`ShellChromeViewModel` → `NavigationService.Back`/`BackTo`; the per-page ← chevrons and
  `IHasBack` are **gone**). (The window's Back order: close the floating bar's ＋ menu → dismiss the topmost
  sheet → nav; band/zoom go through nav — a new sheet needs no special-casing.) The old
  single-screen `LibraryView` was **split**: `LibraryViewModel` = the board grid, `BoardViewModel` = a board's
  asset grid (the GIF/detail/delete logic moved there). `MainWindowViewModel` holds the per-project
  `ThumbnailCache` shared by both. Image-detail is now an **inline full-width masonry band** (see the Masonry
  bullet below), not an overlay or a pushed screen.
  **Opening/creating a project runs off the UI thread** (`ProjectLauncherViewModel.OpenOffUiThreadAsync` wraps
  `ProjectManager.Open/Create` + `EnsureCreatedAsync` in `Task.Run`, behind an `IsOpening` busy overlay): the
  first `DbContext` use **compiles the EF model synchronously** before its first `await` yields — a one-off,
  CPU-heavy cost that froze the shell if run inline. Keep DB-first-touch off the UI thread.
- **Shell chrome: a thin breadcrumb strip + the floating bottom bar (the per-page top bars are GONE).**
  `MainWindow` docks a 34px strip above the page host: `Controls/BreadcrumbBar` renders
  `NavigationService.PageChain` (pages only — band/zoom state steps never appear) as clickable ancestor crumbs
  around a plain current crumb; fitting is the pure, unit-tested `Controls/BreadcrumbTrimmer` — on overflow the
  ellipsis eats from the BASE end ("…est Backup › Terrain Ideas › Buildings"), whole ancestors drop when even a
  3-char stub won't fit (the marker moves onto the next crumb), and the current page trims last. An ancestor
  click = `NavigationService.BackTo(page)` — repeated `Back`s, so the dispose ordering and forward history
  hold — with two deliberate twists: **it IGNORES `IAbsorbsBack`** (absorption protects a single-step gesture
  from popping a page mid-band-collapse; a crumb jump LEAVES the page like a `Push`, whose leave path abandons
  the animation — honouring the absorb stalled the jump after the band closed), and **crumb clicks are gated on
  `Chrome.IsBarVisible`** — the strip is docked ABOVE the page, so no page-level sheet scrim (or the lightbox)
  covers it; ungated it could pop a page out from under an open modal. The **floating bar** (`Controls/FloatingBar` bound to
  `ViewModels/ShellChromeViewModel`, both shell-lifetime) is a bottom-centre pill: **← back** (plain
  `Navigation.Back`, disabled at the root), **🔍 search** (the pill *morphs into the input*), and **＋** (a
  context-menu card rendered ABOVE the pill in the same visual tree — token styles apply, no `Popup` root —
  with a transparent click-away backdrop). Pages feed the chrome through the small `ViewModels/PageChrome.cs`
  contracts — `ICrumbTitled`, `IProvidesSearch` (live `SearchText` + `SubmitSearch` on Enter),
  `IProvidesPlusActions` (a fixed list of `PlusAction`s with observable visible/enabled), `IImmersivePage`
  (the Board's lightbox) — so it stays page-agnostic and is tested against fakes (`ShellChromeViewModelTests`).
  **Search scope is contextual, and it filters the CURRENT grid:** a Board live-filters its images (the
  pre-existing debounced `SearchQuery`) **AND its subfolder cards by name** (instant, no debounce; the
  "+ New folder" tile hides while searching); the Library live-filters its board cards **by name**
  (`BoardCardRef.IsFilteredOut` — image search is done from inside "All images", not from the board grid); the
  launcher live-filters the recents grid (`RecentProjectRef.IsFilteredOut`). Filtered cards are hidden, never
  removed, so covers aren't churned by typing. **While a search is active, the current page's crumb carries a
  live result count** — "Terrain Ideas (2 folders · 12 items found)" / "(x boards found)" / "(x projects
  found)": `CrumbTitle` is computed + re-raised by the page (on query change, on the debounced grid apply, on
  the folder filter), and `ShellChromeViewModel` rebuilds the trail on the current page's `CrumbTitle`
  notification.
  **The bar's search state always mirrors the current page's query** (arriving on a page with a query opens the
  field; ✕/Esc-in-field clears the page's filter as it collapses) — a collapsed bar can never hide an active
  filter. **＋ menu per page:** Projects → New project; Library → Import board + Backup (the remote-sync
  sheet — see the Backup/replication bullet); Board → Sync (hidden until the
  async source load proves ≥1 source, disabled while importing) + New folder; the virtual "All images"/results
  board contributes nothing, which hides ＋ entirely. **The whole bar hides while any sheet is open or the
  lightbox is up:** `SheetHost` raises a bubbling `IsOpenChangedEvent` that `MainWindow` folds into
  `Chrome.IsModalOpen` (also recomputed, posted, on page swaps — a page can leave with its sheet open); the
  lightbox flows through `IImmersivePage.IsImmersive`. **Esc/back order at the window:** close the ＋ menu →
  dismiss the topmost sheet → `NavigationService.Back`. The search field claims Esc itself while open (the
  open-control contract from the Esc bullet above) and collapses. Pages keep a ~96px bottom content inset so
  the pill never permanently covers the last grid row.
- **Settings are shell chrome too (the bar's ⚙).** `Services/UiSettingsStore` persists the desktop head's
  preferences at `%APPDATA%/Hoard/ui-settings.json` — its OWN file, not Core's `settings.json`, which
  `ProjectManager` rewrites whole (recents) and would clobber anything else stored there. One mutable
  `UiSettings` instance is shared app-wide: the sheet (`Controls/SettingsSheet` + `ViewModels/SettingsViewModel`,
  hosted in a shell-level `SheetHost` in `MainWindow`; saves on every change, no Apply/Cancel) writes it, and
  consumers read it live — the import/sync cookie pickers pre-select `DefaultCookiesBrowser` when they open;
  the board's GIF playback reads `GifAutoplay` and `MaxPlayingGifs` (the LRU bound that used to be a
  hardcoded 12, so memory stays capped either way; a Settings save also **trims already-playing GIFs to a
  lowered budget immediately** via `UiSettingsStore.Changed`). **Autoplay is VIEWPORT-driven, never
  realization-driven:** `BoardView` runs a debounced (200ms) scan on `ScrollChanged` of the realized
  containers that actually intersect the viewport, **sorted top-to-bottom and capped at the budget**
  (`BoardViewModel.AutoplayVisibleGifs` → the same `PlayGif` LRU as tapping) — uncapped, a viewport holding
  more GIFs than the budget made every scan cycle the excess through play→evict (decode churn + flicker).
  Playing on `ElementPrepared` was tried and is a trap — the repeater realizes a buffer BEYOND the viewport,
  last, so the off-screen buffer GIFs evicted every visible one from the LRU: memory climbed while nothing on
  screen animated. Related hardening in `AnimatedImageControl`: detach cancels the load and frees frames, so
  re-attach now reloads when it still has a `Source` but no frames (a recycled element re-attaching with the
  same source gets no property change), and detach clears `IsLoading` (a superseded load used to strand the
  spinner). Theme applies through
  `Application.RequestedThemeVariant` (`SettingsViewModel.ApplyTheme`, also called at startup from `App`).
  The **interface scale** (75–150%) is a `LayoutTransformControl` around the WHOLE shell
  (`MainWindow.RootScale`, applied from code-behind — a transform has no DataContext to bind through): a
  LAYOUT transform, so screens re-measure and the masonry reflows its columns at the new size. That's
  user-chosen accessibility zoom — compatible with DESIGN.md's "no global scaling", which bans scaling a
  fixed desktop layout *instead of* reflowing. **Known limitation: popup-rooted UI (ComboBox dropdowns,
  ToolTips, flyouts) renders in separate popup roots that do NOT inherit the layout transform**, so at
  non-100% scale a dropdown renders at system scale beside its scaled anchor — the ＋ menu deliberately lives
  in-tree (no `Popup` root) partly for this reason; fixing the rest needs a popup-scaling strategy.
  `SettingsViewModel`'s ctor **persists its snap-backs** (an out-of-choice stored value is normalised AND
  written back) because consumers read the raw shared `UiSettings` live — display and enforcement must be one
  value. About rows report the app version and the bundled gallery-dl's
  file version (`FileVersionInfo`, deliberately no `--version` subprocess).
- **Import targets one board, and progress is shared state.** The import sheet picks a target board (new —
  created up front via `IngestService.CreateBoardAsync` so the card shows immediately — or existing to merge);
  `IngestService.ImportAsync(targetCollectionId)` links every pin into it instead of auto-foldering by source
  board. A shell-owned **`ImportStatus`** (`ViewModels/ImportStatus.cs`) carries `IsImporting`/`CollectionId`/
  `Text`/`LastImported`/`LastImportedCollectionId`, so the **Library card and the open Board screen show the same
  live count and stream new pins in**. Each streamed pin is **routed to the collection it was actually filed into**
  (`IngestProgress.ImportedIntoCollectionId`, set per item): a loose pin lands on the board grid, a **sectioned pin
  updates its folder card live** (debounced) instead of flashing on the root grid to be reorganised at the end.
  gallery-dl reports no total mid-stream, so progress is a count + indeterminate bar, never a %.
- **Syncing a board re-runs the import per source.** The Board screen's **Sync** action (in the floating bar's
  ＋ menu; visible only for a real board with ≥1 URL'd source) opens a cookie sheet, then `BoardViewModel.SyncAsync` loops the board's
  `CollectionSource` URLs (`LibraryService.GetBoardSourceUrlsAsync`) through `IngestService.ImportAsync(targetCollectionId)`.
  No new download logic — it reuses the whole pipeline, so it pulls in missing/new items and **skips already-held
  AND tombstoned (blacklisted) items** (the `KnownItems` skip-archive includes tombstones, and `ImportAsync`
  re-checks `DeletedAt`). Progress flows through the same `ImportStatus` (inline strip + live streaming + reload).
- **The skip-archive is TARGET-aware, and import re-attaches orphans.** Two import-correctness rules that make
  re-import/sync actually repopulate a board: (1) `GetKnownItemsAsync(targetCollectionId)` pre-skips only pins
  **already in the target board** (+ globally-tombstoned blacklist) — NOT pins merely held in some *other* board
  — so a live pin missing from the target is left for download → dedup-by-hash → link instead of being skipped
  globally. (2) After the crawl, `ReattachOrphansAsync` links **orphaned live assets** (no `CollectionItem` at
  all) that belong to an imported board — matched by the board id in their stored sidecar (`SidecarBoardId.From`,
  shared with the v5 backfill) — into the target. This recovers a **restored-but-uncrawled** image (its pin was
  removed from the Pinterest board, so gallery-dl never re-lists it, but its content + provenance are in the DB).
  Tombstoned orphans are never re-attached.
- **A board can merge several source boards (schema v3/v4).** A local board is one `Collection`; the Pinterest
  boards it gathers pins from are rows in **`CollectionSource`** (the authoritative many-sources list — board
  id/url/name). Importing into a target board records its source(s); a second import into the same board *is*
  the merge. `Collection.Name` is the source/original name; **`Collection.DisplayName` is a local rename
  override** (`CurationService.RenameBoardAsync` writes it, non-destructive, survives re-import) so the shown
  title is `DisplayName ?? Name`. The denormalised `Collection.SourceBoardId`/`SourceUrl` are kept only as a
  "primary source" pointer (`RemoveSourceAsync` re-seeds it from a surviving source so it never drifts).
  **`GetKnownItemsAsync` joins through `CollectionSource`** (a flat join — SQLite can't APPLY a `SelectMany`
  over a nav) so re-crawling any merged source skips pins already held; over-claiming a pin under a sibling
  source is safe (only ever skips content we have). **Per-pin provenance (v4):** `CollectionItem.CollectionSourceId`
  (nullable FK, **ON DELETE SET NULL**) records which source each link came from, set at import. **Remove-source
  hard-deletes that source's images** (no keep option — a source's pins left behind are churn): `RemoveSourceAsync(id)`
  drops the source record and **deletes the attributed live assets outright** — `db.Assets.Remove` (cascades
  links + tags everywhere; content-addressed = gone from every board) + logs a Remove + **recycles each blob to
  the OS bin** (`IFileRecycler`, fallback permanent when none). NOT a tombstone — no placeholder tile, not
  in-app restorable; a re-import re-fetches them fresh. When it's the board's **last** source it also sweeps the
  board's remaining live images (catches pre-v4 un-attributed pins). **`DeleteBoardAsync` is the same** (hard
  delete + recycle, then remove the grouping). Both share `StageAssetRemovals` + `FreeBlobsAsync` and skip
  already-tombstoned assets, so a **per-image delete stays the restorable tombstone** (`DeleteAssetAsync` /
  `RestoreAsync` — unchanged). Routed through a `ConfirmSheet`. When you touch this model, follow the
  additive-schema rules above (v3 backfills legacy single-source boards; tests assert the upgrade DDL matches EF's). **v5 is a data-only step** (no DDL): `SourceAttributionBackfill` attributes pins
  imported before v4 — single-source boards trivially, merged boards by the board id in each asset's stored
  sidecar (`MetadataJson`) — so remove-source's per-source hard-delete works on legacy data too.
- **Nested folders = child collections (schema v7).** A Pinterest *section* / sub-folder is just a child
  `Collection` via the long-present self-referencing **`Collection.ParentId`**; `SourceSectionId` (v7, additive —
  added by a *guarded* upgrade since SQLite `ADD COLUMN` has no `IF NOT EXISTS` and a fresh-model DB is reused to
  simulate older ones in tests) records the source section id on a child so a re-import re-finds it. **Loose pins
  link to the parent; sectioned pins link to the child**, so a board's grid (`GetAssetsAsync(id)` filters by
  `CollectionId`) shows only its loose pins automatically. `GetCollectionsAsync` lists **top-level boards only**
  (`ParentId == null`); `GetChildBoardsAsync(parentId)` feeds the Board screen. **A card's count/size rolls up the
  whole subtree** (its sections count toward the board) — `LibraryService` sums per-collection totals over the
  `ParentId` tree (`LoadSubtreeRollupAsync`; `GetBoardDetailAsync`/`GetBoardAssetShasAsync` are subtree-scoped too),
  and **covers are picked spread** (most-recent · midpoint · oldest via `SpreadSelect`, across the subtree) so a
  board's 3-up — and the "All images"/project collage — pull from a variety of boards, not the latest import.
  Subtree walks share `CollectionTree.SubtreeIdsAsync` (delete/remove-source/known-items/shas) — the rollup is the
  batch in-memory variant for the "all boards" case. **Folders are rendered exactly like top-level boards** — `BoardCard`s (collage + pencil edit) in a `FillWrapPanel` led by a "+ New folder"
  `NewCard`, shown **before the image masonry in one `ScrollViewer`** (a `StackPanel` of [folder grid][masonry];
  the masonry stays virtualized because `MasonryLayout` realizes from `context.RealizationRect`, the effective
  viewport, not the measure height). **Drilling into a folder reuses the whole Board screen** — `ShowBoard(new BoardTarget(childId, …, ParentId: …))` pushes another
  `BoardViewModel`, so nesting works to any depth via the back-stack; an `IResumable.OnResumed` nav hook reloads a
  board's folders + grid when a child is popped (a folder may have been renamed/deleted, or a pin moved out).
  A folder is renamed/deleted via its **card pencil**, the *same* rename/clear-cache/delete flow as editing a
  board from the Library: both view models hold a shared **`BoardCardEditor`** (`ViewModels/BoardCardEditor.cs`)
  that drives the `BoardEditSheet` over a `BoardCardRef` — the Library board edit also lists merged sources (via
  a `loadExtraDetail` hook), the folder edit sets `ShowSources=false`;
  `CurationService.DeleteBoardAsync` **recurses the subtree** (the `ParentId` FK is `SET NULL`, so children are
  removed explicitly, not cascaded) and `MoveAssetWithinBoardAsync` re-points a `CollectionItem` to file a pin
  into a folder (one at a time, via the detail panel). **Auto-import (ingest side) is wired:** `SourceMediaItem`
  carries `SectionId`/`SectionName`/`SectionUrl`; `IngestService.ImportAsync` files a sectioned pin into a child
  folder via `GetOrCreateSectionFolderAsync` (find-or-create by parent + `SourceSectionId`, cached per run) while
  a sectionless pin lands on the board; the section pin keeps the **board's** source attribution, so
  `RemoveSourceAsync` (now subtree-scoped via `CollectionTree.SubtreeIdsAsync`, shared with delete-board) sweeps a
  source's foldered pins too, and `GetKnownItemsAsync` pairs held descendant-folder pins with the root board's
  source ids so a re-sync pre-skips them. **Confirmed working end-to-end** (the user runs it against real boards
  with sections): a plain board crawl emits per-pin section metadata in the sidecar — no section flag on the
  connector — which `PinterestSidecarParser` extracts (it handles either a nested `section` object or a flat
  `section_id`/`board_section_id`, probed defensively since Pinterest's shape drifts; still a graceful no-op for a
  sectionless pin). So importing a board auto-creates a child folder per section and files its pins there.
- **Compatibility / resilience to outside changes (cheap on open, deep on demand).** Older project DBs are
  upgraded by `SchemaInitializer` on open (above). Beyond that: **(marker)** a folder with project data but a
  missing/corrupt `hoard.project.json` is recoverable — `HoardProject.Open` tolerates a malformed marker (derives
  the name); `HoardProject.Adopt`/`ProjectManager.Adopt` rewrite a missing marker, offered only via the explicit
  "open existing folder" path (with a confirm) so "open recent" stays strict. `ProjectManager` prunes recents
  whose folder vanished. **(missing blobs)** a *live* asset whose blob was deleted/moved/altered outside the app
  doesn't crash — the tile detects it lazily (per-tile `File.Exists`, not a bulk scan) and shows a **"file
  missing"** state with a one-click **re-download** (`IngestService.RefetchAsync` → re-fetch from the saved
  source URL; shares `ReDownloadAsync` with restore). Full content-hash verification is **on demand, never on
  open** — the launcher edit sheet's **Verify files** runs `Library/ProjectVerifier` (re-hash every live blob
  + sweep unreferenced store files; **report-only** — on a shared archive a blob can precede this machine's
  catch-up of its op, so an automatic orphan delete could destroy another device's fresh import). Keep open
  cheap: schema + marker + recents only.
- **Recycle, don't delete (Windows only).** `IFileRecycler` (Core, platform-neutral interface) →
  `WindowsFileRecycler` (Desktop, `SHFileOperation` + `FOF_ALLOWUNDO` P/Invoke — platform code stays out of
  Core), **registered in DI only when `OperatingSystem.IsWindows()`** (the P/Invoke throws elsewhere) and
  injected (optionally) into `ProjectManager` **and `CurationService`**; when absent (macOS/Linux, Core tests)
  the fallback is a permanent delete — and the delete confirms/toasts say so honestly via
  `Services/RecycleWording` (keep any new delete UI wording routed through it; a macOS trash implementation
  is future work). **`ProjectManager.DeleteProject`** recycles the project folder; **`CurationService` recycles
  image blobs** on the hard-delete paths (remove-source, delete-board). The lone exception is **per-image delete**,
  which tombstones (keeps a restorable row, frees the blob permanently) — that one is in-app restorable by design.
- **Decoded GIF frames are reference-counted, not cached-with-retention.** `RefCountedCache<T>` (in
  `Infrastructure/`) does single-flight load per key and hands out disposable `ResourceLease<T>` handles; frames
  are freed when the **last** lease is disposed. `AnimatedImageControl` holds exactly one lease and disposes it
  in one place. Memory tracks what's on screen — do not reintroduce a sticky/LRU GIF cache. (This replaced an
  earlier design whose scattered release logic caused a series of leaks.)
- **Thumbnail bitmaps are disposed, not left to finalization.** `AssetTileViewModel.Thumbnail` is an Avalonia
  `Bitmap` = an unmanaged Skia surface whose managed wrapper is tiny, so the GC feels no pressure and merely
  dropping the reference leaks the real memory until lagging finalization. So the VM **owns** its bitmap's
  lifetime: the `OnThumbnailChanged` hook frees the *previous* bitmap **synchronously** on **every** swap
  (replace/recycle/dispose). (It must be synchronous — an earlier version deferred the `Dispose` to a `Background`
  dispatcher tick, which **starved disposal during a fast scroll**: released bitmaps piled up un-freed, 1000+ live
  at once, the working set ballooned to ~940 MB, and the native heap kept that high-water mark — looking exactly
  like "memory never frees". Avalonia's deferred renderer ref-counts the bitmap impl per composed frame, so freeing
  the managed wrapper here — the `Image` rebinds on the `PropertyChanged` that immediately follows — can't free a
  surface mid-draw, so the deferral bought nothing and cost the balloon.) The grid frees off-screen tiles via a
  `BoardView` **`ElementClearing`** handler
  (symmetric with `ElementPrepared`) → `ReleaseThumbnail()` (also resets `_thumbnailRequested` so re-realizing
  re-decodes from the on-disk cache); leaving a board frees them all via `BoardViewModel.Dispose()` (`tile.Dispose()`
  per tile). So in-memory image footprint **tracks what's near the viewport** instead of climbing with every image
  ever scrolled past — same philosophy as the GIF refcount above. The on-disk `ThumbnailCache` is unaffected (it's
  the *decode* cache; this is the *in-memory* one). Don't go back to holding a decoded `Bitmap` per asset for the
  life of the board.
- **The on-disk thumbnail cache has exactly ONE decode width.** Files are keyed `{sha256}_{width}.png`, and the
  width is `ThumbnailCache.Width` (256), owned by the cache — `GetAsync` deliberately takes **no width parameter**,
  so a per-context size can't creep back in. (One did once: card covers requested 240 while tiles requested 256,
  which silently gave ~1,000 images a second, near-identical PNG — a sixth of a real project's thumbnail folder.)
  A context that renders smaller downscales the cached bitmap at draw time (the launcher collage decodes cached
  PNGs to its 240px card width). The ctor sweeps stale-width files in the background on project open
  (`PruneStaleWidths`, strict filename match, unit-tested), so old projects self-heal and a future `Width` bump
  cleans up after itself.
- **Leaving a board must release its memory — the load-bearing pieces (found via ClrMD GC-root analysis).** A
  detached `BoardView` is NOT promptly collected (Avalonia's compositor/`ItemsRepeater` keep references to the
  detached render tree), so anything it transitively holds leaks one board's worth per navigation. **(1) NO raw
  `<ProgressBar IsIndeterminate="True">` anywhere — use `Controls/BusyBar`.** This was the ROOT of the whole-view
  leak (heap-dump-verified; Avalonia #15793/#17192/#15389): Fluent's indeterminate animation is infinite, Avalonia
  keeps it ticking even hidden/detached, and a ticking animation re-registers its composition visuals with the
  `Compositor` every frame — permanently rooting the entire detached page via child→ancestor value-store chains
  (~20–35 MB per back-out, ×3 spinners per tile). `BusyBar` ties `IsIndeterminate` to its own `IsVisible` + attach
  state; bind the busy condition to the **BusyBar's own IsVisible**, never only an ancestor's. **(2) the detail band
  is built lazily** — a `ContentControl` whose `Content` binds `AssetTileViewModel.BandContent` (== `this` only while
  `IsExpanded`, else null), so the band markup exists **only for the expanded tile** (a per-tile band was ~190 MB per
  leaked view). Keep it lazy. **(3) tiles don't pin the board** — `AssetTileViewModel` holds its board-method
  callbacks as *fields* and `Dispose()` nulls them, so a tile the `ItemsRepeater` still retains (it caches its last
  `ElementClearing`/`Prepared` event-args) can't drag the whole `BoardViewModel`. **(4) dispose-before-swap**:
  `NavigationService.Back` disposes the outgoing page **BEFORE** setting `Current` (which detaches the view
  synchronously) — `BoardViewModel.Dispose` fires `ViewTeardown` so the still-attached `ItemsRepeater` runs one last
  layout pass and recycles+detaches its tiles; a *detached* repeater never runs layout again. The ordering is pinned
  by a unit test — don't "simplify" it. **(5) `BoardView.OnDetachedFromVisualTree` tears down** — disposes the
  explicit-`Source` masonry binding, unsubscribes events, nulls `ItemsSource` + `DataContext`. **Every native
  `Bitmap` on a VM has an owner that disposes it eagerly** (tile thumbnails, `AssetDetailViewModel.Preview`,
  board/folder/project card covers — the cards are `IDisposable` with a sticky `IsDisposed` their async cover loads
  check, so a late decode frees its bitmap instead of stranding it). **Async loads guard against dead VMs**:
  `LoadAssetsAsync`/`LoadFoldersAsync` use a `_disposed` flag + monotonic load-sequence (only the latest in-flight
  load applies; a stale/post-dispose resume applies nothing). A DEBUG-only **`Infrastructure/LeakCanary`**
  (Track-at-ctor / MarkDead-at-dispose-or-detach; warns only when dead screens survive a forced GC) makes the next
  leak announce itself — keep new heavy per-screen objects registered with it.
- **Masonry layout** splits the pure packing/visibility math (`MasonryPacker`, Avalonia-free, unit-tested,
  per-column binary search) from the `VirtualizingLayout` shell (`MasonryLayout`). Keep new layout math in the
  packer so it stays testable. **Image-detail is an INLINE full-width band, not a separate screen/overlay**
  tapping a tile expands it (`AssetTileViewModel.IsExpanded`, toggled via
  `BoardViewModel.SelectedAsset`); the packer lays that one item as a **full-width band** (drops below all columns,
  spans the width, then columns resume beneath it — grid packs strictly above/below it, never beside), driven by
  `MasonryLayout.ExpandedIndex` (bound to `BoardViewModel.ExpandedIndex` in `BoardView` code-behind) + a band-height
  model. The item template is **dual-mode** (the `ItemCard` tile when collapsed, the band — big media + the reused
  `AssetDetailViewModel` rail/actions — when `IsExpanded`). **The reflow is animated**, because the visible stutter
  was the *tiles* snapping to make room, not the band: `MasonryLayout` keeps both the band-free (`_basePacker`) and
  banded (`_bandPacker`) packings and **tweens every visible tile's rect** between them over a short eased reflow
  (open 280ms / close 180ms) via a shared `Infrastructure/Tween.cs` lerp (a `DispatcherTimer`+`Stopwatch` value
  tween — used by the reflow *and* the scroll-to-top; Avalonia can't animate a plain field/`ScrollViewer.Offset`).
  Keep band-sizing/packing math in `MasonryPacker` (incl. `MasonryPacker.BandHeight`, unit-tested) — the *pure*
  packer stays untouched so its tests stand. The band fade is **sequenced around** the reflow, not
  concurrent: a `ReflowSettled` event + a per-tile `BandContentOpacity` (a `DoubleTransition` on the band Border).
  **Open** = tiles reflow (band present but opacity 0) → on `ReflowSettled(i)` fade the band in; scroll-to-top runs
  at the start (`ExpandedBandTop`+`TranslatePoint` — also fixes the edge-tile "won't open" case without needing the
  element realized). **Close** (reverse, quicker) is a 3-step handshake so the band fades *before* the tiles move
  and the item never flashes as a stretched tile: `RequestCollapse` → `CollapseStarting` (view fades out) →
  `CommitCollapse` (drops the band from the layout, keeps the tile selected-but-hidden so `IsExpanded` stays true) →
  reflow back → `ReflowSettled(-1)` → `FinishCollapse` (clears selection); `_collapsing` holds `SyncExpandedIndex`
  at -1 through the close, with a fallback timer if the settle signal is missed. Because the band step is reverted
  synchronously by `Back` but its `SelectedAsset` clears only when the animation finishes, two close-window edges are
  guarded: a **second** Back/Esc during the collapse is *absorbed* (`BoardViewModel` implements `IAbsorbsBack`,
  true while `_closing`; `NavigationService.Back` consults it before popping the page) so a rapid double-press
  doesn't pop the whole board out from under the animation; and navigating **away** with the band still open (e.g.
  drilling into a folder via `Push`, whose synchronous revert can't finish the animation on a leaving view) calls
  `BoardViewModel.AbandonBand()` from `BoardView.OnDetachedFromVisualTree` to drop the band state synchronously, so a
  board kept beneath on the stack isn't revealed half-open. **GIF in-tile LRU is unchanged** —
  expansion is additive (played GIFs keep animating in their tiles regardless). **The band is now built lazily** (a
  `ContentControl` bound to `AssetTileViewModel.BandContent`, non-null only while expanded — see the memory bullet
  above), so its markup exists only for the expanded tile; the band GIF binds the per-tile `BandPlaySource`
  (referenced via the band root's tile `DataContext`, `#BandRoot`), non-null only for that one tile, NOT the shared
  `Details.FilePath`. (Pre-laziness the band lived in every realized tile, which is why the GIF `Source` had to be
  gated on `BandPlaySource` — still true, and now also free since the band only exists once.)
  **Gotcha already hit (cost a crash):**
  animating `RenderTransform` with `TransformOperations` via a *code-built* `Animation` throws at runtime (no
  animator resolved) **and the throw is deferred onto the animation timer, so a try/catch can't catch it** — use the
  declarative XAML transition path (e.g. `DoubleTransition`) for any movement, or opacity-only in code; never a
  code-built transform animation. **Increment 2:** (a) tapping the band's media opens
  a **fullscreen zoom/pan `Controls/Lightbox`** — a near-black overlay (`LightboxScrimBrush`/`LightboxForegroundBrush`
  tokens) viewing the **full-res** still (decoded on open, disposed on close) or the GIF; scroll-wheel zooms anchored
  at the cursor, drag pans, double-click fits, scrim/✕/Esc close. **The band AND the zoom are now back/forward
  history steps** (see the Shell-navigation bullet): opening the band `PushState`s it, switching the band's image
  `ReplaceTopState`s, opening the zoom `PushState`s it; **Back / Esc / mouse-5 / the ← chevron all step zoom → band
  → out of the page**, and Forward re-opens them (cross-page too — a `StateStep.Apply` runs against the *rebuilt*
  board by asset id, deferred via `_pendingBandAssetId`/`_pendingZoom` until its async load finishes). So the
  lightbox's ✕/scrim (`CloseLightbox`) and the band's ✕ (`CloseDetails`) and tap-the-open-tile-again all just call
  `NavigationService.Back`. **Esc is handled once, at the window** (`MainWindow` bubble `KeyDown` — see the
  Shell-navigation bullet for why bubble, and the invariant it relies on) as a unified
  "back": dismiss the topmost open `SheetHost` (a transient modal — NOT a history step; `OfType<SheetHost>()`), else
  `NavigateBack`. Because it's window-level it works regardless of focus and on every page — which is why the old
  per-screen Esc machinery (a Board `Escape` KeyBinding + a generic `IModalOverlay` arbiter + `FocusManagement`
  focus-parking + the lightbox `Focus()`ing itself) was **all deleted**; it was treating a focus symptom the
  window-level handler removes. The lightbox closes only via the ✕/scrim (which raise `CloseCommand`). **Lesson: an
  overlay that's conceptually a navigation state belongs IN the back/forward stack, not bolted on with its own key
  handling — that dissolves the focus/Esc problems instead of patching them.**
  Zoom/pan is one accumulating affine `Matrix` set
  **directly** as the media host's RenderTransform (anchored zoom `M·T(-c)·S·T(c)`, pan `M·T(d)`, scale `[fit,8×]`) —
  *not* a code-built animation, per the gotcha above. Driven by `BoardViewModel` `IsLightboxOpen`/`LightboxSource`/
  `LightboxIsGif` + `OpenLightbox()`. (b) **Per-image delete now uses an in-app `SheetHost` note sheet**
  (`IsDeleteSheetOpen`/`DeleteNote`/`CanConfirmDelete` → `ConfirmDeleteAsync`), **replacing the old `DeleteDialog` OS
  window** (deleted) — like the Move / New-folder sheets. (c) **The inline band is responsive**: its inner two-column
  `Grid` is now a `DockPanel` whose info rail docks **Right** (`MasonryLayout.RailWidth`) when wide and **Bottom**
  (capped at `MasonryPacker.StackInfoHeight`, scrolls) below `MasonryPacker.StackBreakpoint` — both states from
  **styles** (a local Dock/Width would beat the style), via a `bandstacked` class bound to `BoardViewModel.IsBandStacked`
  (the view sets it from the grid width using the packer's own breakpoint, so the layout matches the band-height math).
  The outer fade Border is untouched; the old `RailColumnWidth` `GridLength` is gone.
- **Logging.** Serilog → the launching terminal (the WinExe attaches to the parent console) **and**
  `%APPDATA%/Hoard/logs/hoard.log` (rolled daily: `hoardYYYYMMDD.log`). `App` hooks `AppDomain.UnhandledException`
  + `TaskScheduler.UnobservedTaskException` and `Log.CloseAndFlush()`es so an otherwise-silent UI-thread crash
  still lands in the log with a stack trace. Per-import transcripts are written into the project's `logs/`. Read
  the log file directly to diagnose failures rather than asking the user to copy from the UI.

## Conventions

- **Don't commit until it's verified and asked for.** A green build + passing unit tests is **not** enough to
  commit — runtime behaviour (GUI, GIF playback, delete, memory) can only be confirmed by running the app,
  which only the user can do here. Implement → build → test → hand to the user to verify → **wait for them to
  confirm and explicitly ask** before committing. Until then, leave changes uncommitted in the working tree.
- **Commits: Conventional Commits** — `feat:`, `fix:`, `docs:`, `refactor:`, `perf:`, `chore:`, with optional
  scope and a `(wip)` marker for in-progress work (e.g. `perf(wip): …`). End commit messages with the
  `Co-Authored-By: Claude …` trailer.
- **The subject line of a `feat:`/`fix:`/`perf:` commit IS a changelog line** — release-please copies it
  verbatim into the user-facing `CHANGELOG.md` and the GitHub Release notes. So for those three types: state
  the *user-visible effect*, briefly (aim ≤ ~70 chars), one concern per commit — "fix(board): folder covers
  refresh after a pin moves out", not a `+`-chained list of internals; implementation detail, root-cause
  narrative, and review/process notes go in the commit *body* (release-please only takes the subject) or in a
  hidden-type commit. `docs:`/`chore:`/`test:`/`refactor:` never reach the changelog (see
  `release-please-config.json`), so dev-log style subjects are fine there. Avoid landing `feat/fix/perf` with a
  `(wip)` scope on `main` — the changelog would print it as a "**wip:**" entry; use `chore(wip): …` until the
  work is user-ready, then land the finishing commit as the real `feat:`/`fix:`.
- **Never run destructive git** (force-push, `reset --hard`, `clean -f`, `branch -D`, `checkout --`/`.`, history
  rewrites of pushed commits) regardless of permission mode. Don't commit machine-specific absolute paths into
  tracked files.
- **C#:** `_camelCase` for private/protected fields, PascalCase for everything public; file-scoped namespaces,
  nullable enabled, `var` where the type is obvious, expression-bodied members. **British/Australian English**
  in identifiers, comments, and UI strings. Bias toward performance and tight memory (this app is image/GIF heavy).
- **Tests:** xUnit. Core/ingest logic is tested in `Hoard.Core.Tests`; Desktop logic that can be made
  Avalonia-free (e.g. `MasonryPacker`, `RefCountedCache`) is tested in `Hoard.Desktop.Tests` (which uses
  `InternalsVisibleTo`). Prefer extracting a pure, testable core over testing through Avalonia controls.

## Design system

**Read `DESIGN.md` before building or changing any UI.** Hoard uses its **own** Avalonia styles (no
third-party UI library) with [shadcn/ui](https://ui.shadcn.com) as the design reference and
[Lucide](https://lucide.dev) (ISC) icons embedded as geometries. The look is clean/minimal, dark-primary,
and **mobile-first responsive** (design for the narrowest phone width, reflow up — uses Avalonia 12's
`OnFormFactor` / container queries / `ItemsRepeater` reflow). Navigation is a back-stack, one job per screen
(Projects → Library → Board → Image detail), replacing the current sidebar/overlay clutter.

- **`Theme/Tokens.axaml` is the single source of truth** for colours (light/dark), radius, spacing, type, and
  shadow. **Never hardcode** a colour/radius/spacing in a view — bind a token via `{DynamicResource ...}`; add
  a token if one's missing. New components go under `Theme/Controls/` and into the dev component gallery.
- **FluentTheme is RETIRED — `Theme/Theme.axaml` is the app's entire theme, with no base theme
  underneath.** The stock-control templates Fluent used to supply live in `Theme/Controls/`:
  `Chrome.axaml` (Window, PopupRoot, OverlayPopupHost, AdornerLayer, ToolTip, ItemsControl, ListBox),
  `Scroll.axaml` (ScrollViewer + slim buttonless ScrollBar), `ComboBox.axaml`, `ProgressBar.axaml`
  (BusyBar resolves this via `StyleKeyOverride`; its visibility-gated `IsIndeterminate` is what keeps
  the infinite indeterminate animation leak-safe — the templates carry the PART_* contracts the
  controls' code-behind expects, so keep structure when restyling). `Implicit.axaml` (merged LAST)
  aliases the Hoard component themes as the keyed-by-type defaults (Button→HoardButton,
  TextBox→HoardInput, ListBoxItem→HoardListItem, ToggleButton→HoardSwitch), so an un-`Theme=`d control
  still renders. **A stock control with no theme renders NOTHING** — before using a control type new to
  the app (CheckBox, Slider, Menu…), add a `Theme/Controls/` entry for it. The old placeholder
  `danger`/`overlay` App.axaml styles and the Fluent-dependent `ConfirmDialog`/`MessageDialog` windows
  (long dead code) are deleted; `Window`/`PopupRoot` themes seed the inherited `FontSizeBase` (raw
  Avalonia default is 12, so dropping them would silently shrink all text).

## Working in this repo (environment realities)

- **You cannot launch the GUI here.** Build + tests verify logic and that things compile/lay out; runtime
  behaviour (scroll feel, GIF playback, memory) must be confirmed by the user — point them at `hoard.log`.
- **The sandbox may block executing the bundled `gallery-dl.exe`** (third-party binary), including via the test
  runner. Direct diagnostic runs are sometimes allowed; don't tunnel execution through tests to get around a block.
- **Avalonia 12 gotchas already hit:** `ItemsRepeater` is a separate NuGet package; the clipboard API changed to
  `DataTransfer`/`DataFormat` (a read-only `TextBox`'s built-in `Copy()` sidesteps it); a templated
  control's visual state lives on its template parts, so restyle
  `<class> /template/ <part>#<name>` across states (`:pointerover`/`:pressed`/`:disabled`) rather than
  setting `Background` on the control — see the `ComboBoxItem` theme. **In any custom interactive control template, make the inner
  `ContentPresenter`/content `IsHitTestVisible="False"`** so the fill `Border` is the *single* hover/hit
  surface — otherwise the content (text/icon) is its own hit target and dragging across the content↔fill
  boundary fires enter/leave that flickers the control's `:pointerover`/`:pressed` (the fill `Border` must keep
  a hit-testable background, even `Transparent`). See `Theme/Controls/Button.axaml`. **The `Tapped` gesture fires
  for *every* pointer button (left/right/middle/thumb), so a custom click-to-activate surface that opens on
  `Tapped` (the `*Card` controls) must gate it on a primary press — track `IsLeftButtonPressed` from
  `PointerPressed` and bail in the `Tapped` handler when it wasn't the left button — otherwise right/middle/mouse4-5
  clicks all activate it. The scrim's click-away dismiss filters the same way. `Button`/`ListBox` already
  primary-filter, so this only bites hand-rolled `Tapped` controls.** See `Controls/BoardCard.cs`. **`Tapped` also
  needs the press AND release to hit the *same* visual, so a control with a press/hover `RenderTransform` (`scale`,
  origin centre) whose centre can be **off-screen** (e.g. a tall masonry tile only partly in the viewport) will
  shift out from under the pointer on press, the release lands on a different element, and `Tapped` never fires —
  the tile "refuses to open", position-dependently. Don't rely on `Tapped` there: capture the pointer on
  `PointerPressed` and activate on `PointerReleased` (capture forces the release back to the control). Gate on the
  primary button (`IsLeftButtonPressed` at press + `InitialPressMouseButton == Left` at release) and treat a >~12px
  move as a drag. See `Controls/ItemCard.cs` — this was the long-running "edge tiles won't expand" bug, found by
  logging the click chain (press fired, `Tapped` didn't), not by guessing.** **A `BoxShadow` draws
  OUTSIDE the element's layout box, so any surface with one (cards, the `.card` style, badges) must reserve
  room for its shadow and not be self-clipped — else it crops at the element's edge.** Bake a `Margin` ≥ the
  shadow's blur+offset extent onto the shadowed element itself (size it for the *largest* theme — light
  `ShadowRaised` reaches ~30px sideways and ~42px below) and set `ClipToBounds="False"` on its container;
  reserve the room on the component so callers can't forget (a `UserControl` whose bounds equal the card is the
  classic trap). See `Controls/ProjectCard.axaml`. **A raised card (drop shadow and/or hover-`scale`) inside an
  `ItemsControl`/`ItemsRepeater` is cropped to its arranged cell because the item container clips — the
  `ItemsControl`'s per-item `ContentPresenter` and the `ItemsRepeater`'s realized element both clip by default.**
  This is the recurring "card crops when it expands on hover" bug. Fix it on the *grid*, not the card: set
  `ClipToBounds="False"` on the `ItemsControl`/`ItemsRepeater` **and** its `ItemsPanel`, add an
  `<Style Selector="ContentPresenter"><Setter Property="ClipToBounds" Value="False"/></Style>` to the
  `ItemsControl` (the item containers), and inset the scroll content (margin/padding) so edge cards' shadows
  don't reach the `ScrollViewer` viewport clip. Same `ContentPresenter` style the `ToastHost` already uses; see
  `Views/ProjectLauncherView.axaml`, `Views/LibraryView.axaml`, `Views/BoardView.axaml`. **Grid order is by Pinterest pin id (`SourceId`), descending** —
  `LibraryService` fetches in `Id` order then sorts in memory by the numeric pin id, so order is deterministic
  and survives re-import/restore (`Id` is the stable tiebreak; pinless rows sort last). The sidecar carries
  **no per-pin date** (`CreatedAt` is null for Pinterest — only board-level timestamps exist), and SQLite
  can't `ORDER BY` the id as a number anyway; page-aware sorting would need a stored numeric sort key.
