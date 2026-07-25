# Roadmap & status

Progress tracker so a fresh session can resume. Architecture/conventions are in `CLAUDE.md`; the
user-facing model is in `README.md`. Keep this file current as work lands.

**Status (2026-07-25):** Phases 1–2 complete (shell chrome, nested folders, inline detail band + zoom,
memory work — see the session notes below). **Release automation live** (`da3b993`/`3f1f877`: CI +
release-please + win/macOS build matrix — see CLAUDE.md "CI & releases"; first release PR will be v0.1.0).
**Phase 3's foundation is BUILT: archive format v2** — increments **P0–P3 of `SYNC-DESIGN.md`** landed as
`9c1aeb1` (plus `d03c5c0` cross-platform blob paths, `405335f` macOS delete fix), all **pushed**. The
project folder is now static-only (marker + store + append-only per-device op segments); each machine
derives a rebuildable SQLite index under app data (schema **v8**: uids + `ArchiveOps`); legacy projects
migrate behind a launcher confirm; two machines converge through the segments — **NAS multi-machine
works**. User-verified on real projects: Windows-migrated NAS project opens on the Mac.

**This session — SYNC-DESIGN P4, UNCOMMITTED (needs runtime verification):** everything but compaction.
(1) `ArchiveRebuilder` **deleted** — rebuilds run through `ArchiveSync.CatchUpAsync`, the ONE apply
semantics (the round-trip tests now write real segments and catch up into a fresh index — the production
path). (2) The catch-up loop commits in **batched transactions** (~500 ops per fsync; per-op SaveChanges
kept inside so later ops' lookups see earlier rows — that part is load-bearing, don't "optimise" it away).
(3) Op payload `relativePath`s canonicalised to `/` at emission (`ArchiveLog.CanonicalPath`). (4) One
marker writer (`HoardProject.WriteMarker` serialises project state; the four hand-copied initialisers are
gone). (5) Adopt rewrites the marker then re-routes through the normal open → a v1 adoptee now gets the
migration offer. (6) Launcher cards show "Not opened on this computer yet" instead of zeros
(`ProjectStatsReader` returns null when there's no local DB). (7) **Derived data relocated**: thumbnails/
logs/download-archive live under `%APPDATA%/Hoard/projects/<id>/`, resolved via `ProjectManager.
ThumbnailsRootFor`/`LogsRootFor`/`DownloadArchivePathFor` (v1 keeps its in-folder layout);
`TidyMigratedFolder` sweeps/relocates what older builds left in a v2 folder (thumbnails + skip-archive
deleted, transcripts moved); `DeleteProject`'s existing state-root removal now covers them. (8) **Verify
files** in the launcher's project edit sheet → `Library/ProjectVerifier` (re-hash live blobs, sweep
unreferenced files; **report-only** — an orphan can be another device's not-yet-caught-up import, so no
auto-delete). **User-verified on the real project** (migration tidy, verify, NAS replacement with the
clean local v2 folder). **Segment rotation landed next session (2026-07-26, uncommitted):** a device's
stream cuts into chapters at 4 MB — `<deviceId>.jsonl` is chapter zero, continuations
`<deviceId>.00001.jsonl`… — the writer appends only to the highest chapter and **nothing is ever
renamed**, so a closed chapter is an immutable object (the S3/B2-shaped prerequisite); readers/flush
span the chain (`ArchiveRotationTests`, 5 tests). **Remaining in P4:** compaction only (deferred —
single-user, revisit when log size hurts). **Next:** **P5** (S3/B2 remotes, mobile).

**Code-review pass on the P4 batch (multi-angle; 10 confirmed findings, all fixed):** catch-up pending
detection is now a per-device **set difference** against held rows, not a MAX-seq watermark — a batch
rollback behind a later-committed seq re-pends and heals instead of being buried forever; an unappliable
op is **skipped-and-recorded** (EF's per-SaveChanges savepoint keeps the batch intact) instead of
poisoning its whole batch on every open (+`ArchiveCatchUpTests` for both); the tracker is cleared at
every batch commit (a big first build was heading O(N²) DetectChanges); the v2 tidy sweep only removes
**quiescent** caches (untouched >30 min — a pre-P4 sibling machine mid-import on a shared archive keeps
its live skip-archive/thumbnails); **verify catches the index up from the segments first** (a stale
index reported another machine's deletions/imports as phantom corruption) and **refuses while an import
runs** (in-flight blobs read as unreferenced); the verify guard is per-card (one long verify no longer
deadens other cards' buttons, and a recents refresh can't clear a live run's progress); the stats-null
card wording distinguishes an unindexed v2 ("not opened here") from a v1 whose `hoard.db` is gone
(opening would create an empty DB — data loss, not indexing); Clear cache's marker peek moved off the
UI thread; the five hand-copied v1/v2 path resolutions centralised (`AppPaths.LocalDatabasePath`/
`LocalThumbnailsDir` + `HoardProject.StoreDir`); and open/create/adopt/verify failures now log to
`hoard.log` (they were toast-only — a real NAS open failure left no trace to diagnose from). Tests:
**126 Core / 84 Desktop**, green; build clean.

**Earlier session — nested folders (Pinterest sections):** a section/sub-folder is a **child `Collection`** (via the
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
`PinterestSidecarParser` section probe. **Phase 0 (done — confirmed against real boards with sections):** a plain
board crawl emits per-pin section metadata in the sidecar (no section flag needed on the connector); the parser
handles either a nested `section` object or a flat `section_id`, so importing a board auto-creates a folder per
section and files its pins there. **Live-routing + card counts:** imported pins now
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
**Committed as `56d8689`** (schema **v7**). Tests: **95 (Core) + 29 (Desktop)**, green; build clean. Section
auto-foldering is **confirmed working** against real boards (Phase 0 done). **Next:** image-detail Increment 2 (zoom/pan, in-app delete sheet, narrow stacking); retiring FluentTheme; deeper review
refactors. Material-shader background was dropped earlier (don't resurrect).

**Review follow-ups:** (1) **Done** — the folder-edit block is extracted into a shared `BoardCardEditor`
(`ViewModels/BoardCardEditor.cs`), used by both `LibraryViewModel` (board cards) and `BoardViewModel` (folder
cards) over a `BoardCardRef`; the Library board edit adds its merged-source list via a `loadExtraDetail` hook,
the folder edit passes none. (2) The
**last-source** `RemoveSourceAsync` sweep deletes the board's whole subtree incl. user-filed (null-attribution)
folder pins — this is **intended** ("remove the board's last source = empty the board, no orphaned churn"); a
*multi-source* removal now spares user-moved pins (their attribution is detached on move). (3) `ReattachOrphans`
re-files a restored sectioned orphan onto the **root**, not its section (the sidecar carries no section id).

## Inline image-detail, Increment 1 — COMMITTED `5dbf29c` (on `95712cb`), user-verified

The image-detail is **not** a separate pushed screen — tapping a tile **expands it inline** into a full-width band
in the masonry (Pinterest-closeup style), with the grid packing strictly **above and below** it. **Done + tested:**
`MasonryPacker` lays one item as a full-width band (drops below all columns, spans full width, resumes columns
below) — 6 new unit tests cover geometry, visibility, brute-force-with-band, and the no-op guards; `MasonryLayout`
drives it via an `ExpandedIndex` property + a band-height model (wide: image left of a 340px rail; height from the
real aspect, capped). **VM:** `AssetTileViewModel.IsExpanded`; `BoardViewModel` toggles expansion on tap
(`SelectedAsset`), keeps `ExpandedIndex` synced as tiles stream in/out, and the **in-tile GIF LRU is unchanged**
(played GIFs keep animating whether expanded or not). **View:** the item template is dual-mode (tile when
collapsed, the band — big media + the reused detail rail/actions — when expanded); the old right-side overlay is
removed; the layout's `ExpandedIndex` is bound to the VM in code-behind. **Tests: 95 (Core) / 35 (Desktop) green.**

**Crash — FIXED (user-confirmed):** the band-appear animation was a fade **+ scale**, but animating
`RenderTransform` with `TransformOperations` via a *code-built* `Animation` doesn't resolve an animator at runtime
and **threw on every expand → crashed the app**. Worse, the throw is **deferred onto the animation timer**, so a
try/catch around `RunAsync` can't catch it — it has to be opacity-only in code, or use the declarative XAML
transition path. Fixed to opacity-only; `App` now also hooks `AppDomain.UnhandledException` +
`TaskScheduler.UnobservedTaskException` and `Log.CloseAndFlush()`es (the crash hadn't been logged — an unhandled
UI-thread exception terminated the process before Serilog flushed), so any future crash lands in `hoard.log`.

**Edge-tile "band won't open" — FIXED (root cause found via logging, after two wrong guesses).** Symptom:
clicking a tile whose top/bottom protruded past the viewport showed the press state but the band never opened;
repeated clicks didn't help. **Instrumented the whole click→open→expand chain** with temporary Serilog lines —
the log was decisive: failing tiles fired `PointerPressed` every time but **`Tapped` never fired**, so
`ActivateTile` was never called (the open/layout/scroll path was always fine). Root cause: Avalonia's `Tapped`
needs the press **and** release to hit the **same visual**, but `ItemCard`'s press/hover `RenderTransform`
(`scale(0.97)`/`scale(1.03)`, origin centre) shifts a tile whose **centre is off-screen** out from under the
pointer, so the release lands on a different element and `Tapped` is never raised — exactly why it was
position-dependent. **Fix:** `ItemCard` no longer uses `Tapped`; it **captures the pointer on press and activates
on release** (capture forces the release back to the card regardless of the shift — immune to the cause, not a
theory about it). Primary-button-only + inner-button (Unload) exclusion preserved; a >12px move is a drag, not a
tap. (The earlier `FillVisible` always-realize-the-expanded-index is kept as a correct safety measure — the band
must be realized even when packed beyond the buffer — but it was **not** what fixed the open bug.)

**Animated masonry reflow (the real polish — the band fade alone wasn't the issue).** The "instant stutter" was
the **tiles snapping** to their new positions to make room — a `VirtualizingLayout` has no built-in reorder
animation. Now `MasonryLayout` **tweens** the tiles between the band-free packing (`_basePacker`) and the banded
packing (`_bandPacker`) over a short eased reflow (open 280ms / close 180ms), driven by a `DispatcherTimer` +
`Stopwatch` (frame-rate-independent), lerping every visible tile's rect by a `0→1` factor (both packers kept; the
*pure* `MasonryPacker` is unchanged so its tests stand). The band fade is **sequenced around** the reflow via a new
`ReflowSettled` event + a per-tile `BandContentOpacity` (a `DoubleTransition` on the band Border):
- **Open:** tap → tiles reflow to open the gap (band present but `BandContentOpacity 0`, invisible) → on
  `ReflowSettled(i)` the view **smoothly scrolls** the band's top to the viewport top **and** fades the band in,
  together. The scroll is its own eased manual tween (`DispatcherTimer`+`Stopwatch` lerp of `GridScroll.Offset.Y`,
  `CubicEaseInOut`, 280ms) — **never a snap**, and it runs **after** the tiles finish moving, not before (an earlier
  snap-at-start felt jerky and defeated the animation). Target = `ExpandedBandTop` (built **eagerly** on expand from
  the cached aspects+width) → `TranslatePoint` into the scroll viewport; running it at settle means the extent has
  grown to full band height, so even a near-bottom band reaches the top (no clamp). Do **not** put a transition on
  `ScrollViewer.Offset` itself — that would lag every normal wheel/drag scroll.
- **Close (reverse, quicker):** a 3-step handshake so the band fades out *before* the tiles move and the item never
  flashes as a stretched tile mid-reflow. `BoardViewModel.RequestCollapse` (✕/Esc/tap-again) → `CollapseStarting`
  → view fades `BandContentOpacity→0` → `CommitCollapse` (drops the band from the layout, keeps the tile
  selected-but-hidden so `IsExpanded` stays true) → tiles reflow back → `ReflowSettled(-1)` → `FinishCollapse`
  clears the selection. `_collapsing` makes `SyncExpandedIndex` report -1 through the close; a fallback timer
  finalizes if the settle signal is ever missed.

Kept the fade **opacity-only via a declarative `DoubleTransition`** (not a code-built transform animation — see the
crash note). The old `RevealBand`/`BandAppear` code-behind is gone.

**Grid padding — FIXED.** Tiles had a left inset but cropped on the right. The masonry fills its width edge-to-edge,
and `ScrollViewer.Padding`'s horizontal component cropped the right (scrollbar/padding placement — the *measured*
width was already correct, confirmed via logging, so it wasn't an overflow). Moved the horizontal inset onto a
content `Margin="10,0"` (reliable, symmetric) and made the SV padding vertical-only (`0,10`, keeping the
scroll-to-top math that reads `Padding.Top`).

**Status: user-confirmed working** (open from any tile incl. edge tiles, symmetric padding, right/middle-click
don't open). Diagnostics removed.

**Code-review pass (xhigh, 10 angles) — all findings addressed.** (1) **GIF multiplication, real regression:** the
band's `AnimatedImageControl` is in every realized tile's template and loads on `Source` regardless of visibility,
so binding it to the shared `Details.FilePath` decoded/played the selected GIF in *every* collapsed tile. Fixed
with a per-tile `AssetTileViewModel.BandPlaySource` (`IsExpanded && IsGif && !IsDeleted ? path : null`) bound via
`#ItemRoot` — only the one expanded tile loads it. (2) **scroll tween not cancelled** → `_scroll.Stop()` on
close/switch. (3) **timers not torn down on Pop** → `BoardView.OnDetachedFromVisualTree` stops the scroll tween +
`MasonryLayout.StopAnimation()`. (4) **`_collapsing` could stick true** (reload mid-close) → reset on every
selection change. (5) **stale `Details` on a direct A→B switch** → clear `Details` at the start of
`LoadDetailsAsync`. (6) **opacity set by layout index** → set on `SelectedAsset` directly. (8) **empty band on a
slow detail load** → `IsDetailLoading` spinner. (9/10) **two hand-rolled tween loops** → one shared
`Infrastructure/Tween.cs`; dead `onArrived` plumbing gone. (12) **band-height math** moved into the unit-tested
`MasonryPacker.BandHeight` (+3 tests). (13) **`RailWidth 340` duplicated** → single `MasonryLayout.RailWidth` /
`RailColumnWidth` (XAML uses `{x:Static}`). (14) **capture blocked touch drag-scroll** → `ItemCard` releases
capture on a >12px drag. **(7)** programmatic `SelectedAsset=null` (move/reload) collapses instantly with no fade —
**intentional** (the tile is being removed/rebuilt; #4 prevents the stuck state). **(11)** the eager `BuildBand` in
`OnExpandedChanged` was flagged redundant but is **load-bearing** — a direct A→B switch settles *synchronously*
(factor already 1) before any measure builds the band packer, so `ExpandedBandTop` would be null and the switch
wouldn't scroll; kept. **Tests: 95 (Core) / 38 (Desktop) green. Committed `5dbf29c`.**

**Increment 2 — COMMITTED `6697899`, user-verified.** Three pieces:
- **Fullscreen zoom/pan lightbox** (`Controls/Lightbox.{axaml,cs}`): tapping the band's media opens a near-black
  overlay (new `LightboxScrimBrush` / `LightboxForegroundBrush` tokens) that views the **full-resolution** image
  (or the GIF via `AnimatedImageControl`). Scroll-wheel zooms anchored at the cursor, drag pans, double-click
  resets to fit; scrim-click / ✕ / Esc close. Zoom/pan is one accumulating affine `Matrix` set **directly** as
  the media host's RenderTransform (never a code-built RenderTransform `Animation` — the deferred-throw gotcha):
  anchored zoom `M·T(-c)·S·T(c)`, pan `M·T(d)`, scale clamped `[fit, 8×]`. Media is decoded on open and freed on
  close (the still bitmap disposed; the GIF lease released by nulling `Source`). Wired off `BoardViewModel`
  `IsLightboxOpen`/`LightboxSource`/`LightboxIsGif` + `OpenLightbox()`/`CloseLightboxCommand`; the band media's
  `Tapped` (primary-gated) triggers it.
- **In-app delete note sheet** replaces the `DeleteDialog` **OS window** (files removed): a `SheetHost` (like
  Move / New folder) collecting the required tombstone reason — `IsDeleteSheetOpen`/`DeleteNote`/`DeleteTargetTitle`
  + `CanConfirmDelete` (Delete disabled until a reason is typed) → `ConfirmDeleteAsync` → the unchanged
  `DeleteSelectedAsync(note)`.
- **Responsive stacked band:** the band's inner two-column `Grid` became a `DockPanel` whose info rail docks
  **Right** (fixed `MasonryLayout.RailWidth`) when wide and **Bottom** (capped at `MasonryPacker.StackInfoHeight`,
  scrolls) below the packer's `StackBreakpoint` — both states from **styles** (a local Dock/Width would beat the
  style), toggled by a `bandstacked` class bound to `BoardViewModel.IsBandStacked`, which the view sets from the
  grid width using the packer's own breakpoint (so the layout matches the band-height math). The verified
  open/close/fade machinery (the outer Border) is untouched; the old `RailColumnWidth` `GridLength` is gone.

**Code-review pass (xhigh, 10 angles) + Esc-bug fix.** The Esc-saga root cause (found by instrumentation, not
guesswork): the board's `Escape` KeyBinding is the *only* reliable Escape receiver and fires **only while focus
is inside `BoardView`**; the lightbox grabbed focus on open but never returned it, orphaning focus on close so a
second Esc died. Fixed by making `BoardViewModel.EscapeCommand` a **single overlay-stack arbiter** (dismiss
topmost: lightbox → sheets → band), restoring focus to the (now focusable) `GridScroll` when the lightbox closes
(`BoardView.OnVmPropertyChanged`), and adding an **`IModalOverlay`** marker (lightbox + every `SheetHost`) so the
mouse-back guard treats any open overlay as modal. Also fixed from the review: file-missing guard on
`OpenLightbox`; close the lightbox when the selection clears (stale-after-reload); `_loadId++` on
detach/clear so an in-flight decode can't strand a bitmap on a popped control; ignore non-vertical wheel deltas;
dropped the delete sheet's forbidden fixed `Width`; dead `x:Name`/comment removed.
**Second code-review pass (xhigh) → generic overlay refactor.** The first arbiter was still a hand-list and had
already drifted (the folder edit/confirm sheets weren't in it), and focus-restore was lightbox-only. Reworked to a
**generic** model: `IModalOverlay` gains `Dismiss()`; the board's `Escape` arbiter delegates to the view, which
dismisses the *topmost open* `IModalOverlay` (any sheet/lightbox, z-order) else collapses the band; focus is parked
on the grid via `FocusManagement.ParkFocus` whenever **any** overlay's `IsOpen` flips false (every close path), so a
new overlay is covered with no extra wiring. Also fixed: the phantom-pan (clear `_panning` on `PointerCaptureLost`
+ on open). Verified-and-dropped: double-click-to-fit is **not** broken (Avalonia `Gestures` runs on `RouteFinished`
with no `e.Handled` check — confirmed in source); pan-at-fit being free is intentional (double-click recentres).
**Deferred review follow-ups (still tracked):** cap the lightbox decode width (needs the asset's natural width
plumbed so it never upscales); pause the band GIF while the lightbox occludes it; show a busy spinner on the
lightbox's GIF path; let `MasonryPacker` own the stacked-vs-wide *predicate* (not just the breakpoint constant); add
the `Lightbox` to the dev component gallery; consider deferring the still-bitmap `Dispose` off the close path
(possible compositor race); minor mirror-state cleanups (`LightboxSource`/`DeleteTargetTitle`).

**Band + zoom folded into back/forward navigation; Esc = Back (supersedes the overlay refactor above).** Rather than
keep patching the Esc/focus machinery, the band and zoom are now **history steps** in `NavigationService`: a step is
a `PageStep` or a `StateStep` (`Apply`/`Revert` against `Current`). Opening the band `PushState`s it, switching its
image `ReplaceTopState`s, opening the zoom `PushState`s it; `Back`/`Forward` revert/re-apply — **forward re-opens
them even across page boundaries** (state applies to the rebuilt board by asset id, deferred until its async load
finishes). **Esc / mouse-5 / ← chevron are one unified "back"** handled at `MainWindow` (a window-level **bubble**
`KeyDown` — a focused popup/IME that wants Esc gets it first; only an unclaimed Esc reaches the window): dismiss
the topmost open `SheetHost` first (sheets are transient, not history), else `NavigationService.Back`. This **deleted** the now-moot machinery — the Board `Escape`
KeyBinding/arbiter, `IModalOverlay`, `FocusManagement.ParkFocus`, the lightbox `Focus()`-on-open, the mouse-back
swallow-guard — dissolving the focus/Esc problem instead of patching it. `NavigationService` `Pop`→`Back`,
`GoForward`→`Forward`; +`PushState`/`ReplaceTopState`/`DropCurrentStates`, +6 unit tests (incl. cross-page forward).
**User-confirmed the navigation works** (within-board + cross-page back/forward, Esc=Back, sheets dismiss first).

**Memory growth — diagnosed + fixed + user-confirmed.** Symptom: decoded images "never released; memory climbs as
more load; expected to free when leaving a board." Found and fixed in layers, each pinned down by instrumentation
(not guessed) — the last layer needed **ClrMD GC-root analysis** to crack. The fixes, in order of impact:
1. **Thumbnail `Bitmap`s never disposed** (`AssetTileViewModel.Thumbnail` = unmanaged Skia). Now the VM owns the
   lifetime: `OnThumbnailChanged` disposes the previous bitmap **synchronously** (a `Background`-tick defer *starved*
   under fast scroll and ballooned to ~940 MB); a `BoardView` `ElementClearing` handler `ReleaseThumbnail()`s
   off-screen tiles (re-decode from the on-disk cache); `BoardViewModel.Dispose()` disposes every tile. Also fixed a
   late-decode orphan (a decode finishing after its container recycled). → in-memory footprint tracks the viewport.
2. **Disposed boards stayed rooted → never collected** (each dragging its thousands of tiles/`AssetView`s). The GC-root
   chain showed the tile's `RelayCommand` closure captured the board's methods (`ActivateTile`/`UnloadGif`/`RefetchTile`),
   so a tile Avalonia's `ItemsRepeater` still held (it caches its last `ElementClearing`/`Prepared` event-args, pinning
   the last container) kept the whole board alive. Fix: the tile holds those callbacks as **fields** and `Dispose()`
   nulls them. Plus `BoardView.OnDetachedFromVisualTree` teardown (dispose the explicit-`Source` masonry binding,
   unsubscribe events, null `ItemsSource` + `DataContext`) and `BoardViewModel.Dispose` clears `CollapseStarting`.
3. **The detached `BoardView` itself isn't promptly collected** (Avalonia compositor/`ItemsRepeater` keep the render
   tree), and the heavy thing it dragged was the **detail-band markup built into every realized tile** (~33 `Border`s
   ×150 tiles ≈ 190 MB/nav). Fix: the band is now **lazy** — a `ContentControl` bound to `AssetTileViewModel.BandContent`
   (non-null only while expanded), so the band markup exists only for the expanded tile.
4. **The ACTUAL root of the whole-view leak (found by a ClrMD GC-root dump, corroborated by Avalonia #15793/#17192/
   #15389): infinite `IsIndeterminate` ProgressBar animations.** Every tile had 3 hardcoded-indeterminate spinners;
   Fluent animates them forever, Avalonia keeps them ticking even hidden/detached, and a still-ticking animation
   re-registers its composition visuals with the `Compositor` every frame — permanently rooting the whole detached
   page (each board back-out leaked ~20–35 MB). Fix: **`Controls/BusyBar`** (a ProgressBar whose `IsIndeterminate`
   follows its own `IsVisible` + attach state) replaced every raw indeterminate bar app-wide — the bug class is now
   unrepresentable. Supporting fixes: `NavigationService.Back` disposes the outgoing page **before** swapping
   `Current` (view still attached → `ViewTeardown` forces the `ItemsRepeater` to recycle+detach its tiles; ordering
   pinned by a unit test); eager native-bitmap disposal extended to `AssetDetailViewModel.Preview` and the
   board/folder/project card covers (previously never disposed); `SyncAsync` observes the dispose token. A permanent
   DEBUG-only **`LeakCanary`** (warns only when disposed/detached screens survive a forced GC) replaces the ad-hoc
   scaffolding, so the next leak announces itself.

User-confirmed: tiles load smoothly, band/zoom intact, memory tracks the visible set under heavy scrolling, working
set drops on board-exit and stays flat across boards. All diagnostic scaffolding (the `[MEM]`/`[LEAK]`/`[REALIZE]`/
`[TEARDOWN]` logging, forced GC, `WeakReference` trackers, `Microsoft.Diagnostics.Runtime`) stripped back out.
Documented in CLAUDE.md.

**Code-review pass #3 (xhigh, 10 finder angles → 10 verifiers → gap sweep) on the full batch — 15 verified findings
fixed, user-verified.** The clusters: (a) **async completions guarded against dead/buried VMs** — `LoadAssetsAsync`/
`LoadFoldersAsync` get a `_disposed` + load-sequence supersede guard (a stale resume used to refill a disposed
board's tiles and mutate nav history from off-screen; the history purge now also requires `_nav.Current == this`);
cover loads (`BoardCardCovers`, launcher recents) check a sticky `IsDisposed` per card and free late-decoded bitmaps
instead of stranding them; `RestoreSelectedAsync` observes the dispose token. (b) **nav state-steps got a protocol**
— `StateStep.Apply` returns whether it took effect (ghost steps for vanished assets are structurally impossible;
+tests), `Forward` absorbs via `IAbsorbsBack` during the band collapse (mirror of Back), `DropCurrentStates` drops
only the TOP run of forward states so backed-out **pages** survive an in-page reload (the forward button used to die
on any reload), the pending band/zoom latches clear in the Reverts/`AbandonBand`, `OpenLightbox` is gated on
`_closing`. (c) **singles** — the delete sheet captures its target at open (a reload under the open sheet made the
delete a silent no-op); ingest items are atomic once the blob lands (a cancel mid-item could orphan a blob or
permanently resurrect a tombstoned one); `SheetHost`'s own Esc handler deleted (it dismissed the *underlying* edit
sheet beneath a floating confirm — the window's topmost-sheet sweep is the single path) and sheets autofocus their
first `TextBox` on open; `Lightbox.DoubleTapped` is primary-gated; sync-cancel now toasts instead of passing off a
partial sync as complete. Cleanup: `CardTiles.DisposeAndClear` dedup, `BusyBar` slimmed to the `OnPropertyChanged`
idiom, dead usings/`Focusable` removed, BusyBar added to the gallery, CLAUDE.md's Esc wording corrected to bubble.

**Build + tests green (95 Core / 54 Desktop).**

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

- ~~**Sub-boards / sections**~~ — **done** (schema v7 nested folders; folder cards on the Board screen, drill-in
  via the back-stack, auto-foldering on import — see Status above).
- ~~**Image-detail as a pushed screen**~~ — **done, as an inline band instead** (the full-width masonry band +
  band/zoom as history steps; delete moved to an in-app note sheet, `DeleteDialog` deleted). Still open from
  this bullet: **retire FluentTheme + the placeholder `accent`/`danger`/`overlay` styles** (the screens all use
  the token theme now; the base theme is still layered over Fluent).
- ~~**Restore project search**~~ — **done** (the floating bar's contextual search: a board live-filters its
  images + folder names, the Library filters board cards, the launcher filters recents; see the shell-chrome
  session below).
- **Manual collections** (user-created, not just source boards) — slots into the redesigned Library screen.
- **Human-readable export/mirror** (candidate, from user feedback 2026-07-25): an on-demand "Export
  board…" (or maintained read-only mirror) that materialises `Board/Folder/image` trees from the index —
  the browsable-in-Finder view people expect. The archive's `store/` itself stays content-addressed:
  sha-addressing is what gives cross-board dedup, immutable files (ops reference blobs by hash forever;
  renames are metadata ops, not mass file moves), and OS-portable paths (no invalid-name/length/case
  collisions from board titles).
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
- ~~**`SyncAsync` re-entrancy + `RefetchTile` cancellation**~~ — **done:** `SyncAsync` refuses to start while
  `ImportStatus.IsImporting` (no clobbering a running Library import); and `BoardViewModel` holds a dispose-linked
  `CancellationTokenSource` passed to `RefetchAsync`, cancelled (+ disposed) in `Dispose`, so navigating away
  mid-refetch aborts the download and the `OperationCanceledException` is swallowed instead of mutating a dead VM.
- ~~**`AssetTileViewModel.EnsureThumbnailAsync` does `File.Exists` inline**~~ — **done:** the missing-file probe
  runs in `Task.Run` off the realising thread (continuation resumes on the UI thread for the bound state), so a
  slow drive can't jank masonry scroll.
- ~~**`GetBoardDetailAsync` runs the per-source count query even for single-source boards**~~ — **done:** the
  per-source count query is skipped unless the board merges ≥2 sources (single-/zero-source boards never use it).
- **Last-source sweep + manual collections:** `RemoveSourceAsync`'s last-source branch hard-deletes *every*
  remaining live pin; once manual (non-source) collections land, exclude manually-added images from the sweep.
- ~~**Recycle path doesn't prune empty shard dirs**~~ — **done:** `IMediaStore.PruneEmptyShards` exposes the
  store's empty-parent pruning; `CurationService.FreeBlobsAsync` calls it after the batched recycle, so the
  recycle path tidies `ab/cd` dirs like `DeleteAsync` does (covered by a `NestedBoardTests` assertion).
- **Denormalised `Collection.SourceBoardId`/`SourceUrl` "primary pointer"** is now write-mostly (only the
  auto-folder import path reads it); fold it into a computed `Sources.First()` or drop it to remove the
  two-sources-of-truth maintenance.

## Later phases

- **Phase 3 — multi-device archive + sync: `SYNC-DESIGN.md` is the plan of record; increments
  P0–P3 are DONE** (committed `9c1aeb1`) **and P4 is done except compaction** (this session,
  uncommitted — see the Status block). The project folder is 100% immutable files (content-addressed
  blobs + per-device append-only op segments); SQLite plus every derived cache is per-machine app-data
  state; NAS multi-machine works. Remaining: segment compaction (deferred) and **P5** (S3/B2 remotes as
  the same format over object storage; the mobile head reuses all of it).
- **Phase 4 — mobile:** extract Core behind a `Hoard.Server` (ASP.NET Core minimal API, does ingestion
  server-side since mobile can't spawn gallery-dl); Avalonia mobile client with background sync.
- **Phase 5 — capture + more connectors:** TS browser extension (in-page "save to Hoard"); more sources
  behind the same `ISourceConnector`.

## Deferred / known tech-debt

- ~~**From the archive-format (P0–P3) code review**~~ — **all six deferred findings fixed in the P4
  session** (rebuilder unified/deleted, batched catch-up, canonical paths, "not opened here" cards,
  Adopt migration offer, one marker writer — see the Status block).
- **Segment compaction** (the one P4 item deliberately not built; rotation itself landed 2026-07-26): a
  compacted snapshot segment is safe once every known device has applied the retired chapters —
  single-user, so deferred until op-log size actually hurts.
- **`ProjectVerifier` is report-only**: orphaned store files are listed, never deleted (an "orphan" can
  be another device's blob whose ops this machine hasn't caught up yet). If a safe sweep is ever wanted,
  it needs an "all devices caught up" proof first.

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
- **Deferred cleanups from the shell-chrome xhigh review** (all report-only; the confirmed bugs were fixed
  pre-commit): a shared card-filter helper (the trim→`IsFilteredOut`→crumb-count dance is triplicated across
  launcher/Library/Board, with "reapply after rebuild" needed at every grid-rebuild site); a shared
  `Infrastructure/Debouncer` (the `_gifScan` timer joins two CTS/Task.Delay debouncers in `BoardViewModel`);
  a `Plural(n, noun)` formatter (~8 hand-rolled `n == 1 ?` ternaries); a shared `JsonFile.TryLoad/Save`
  (UiSettingsStore repeats ProjectManager's + HoardProject's pattern; none write atomically); making
  `UiSettings` observable per-property instead of the store-wide `Changed` (each live consumer hand-diffs);
  a `FloatingBarClearance` token for the ~96px bottom insets hardcoded in three views; a single
  dismissable-transient arbiter (the ＋ menu is special-cased in `MainWindow.Back/Forward` ahead of the sheet
  sweep); `MasonryLayout` exposing its visible index range so GIF autoplay needn't re-derive visibility with
  a visual-tree walk. **Known limitation (documented in CLAUDE.md):** popup-rooted UI (ComboBox dropdowns,
  ToolTips) doesn't inherit the Settings interface-scale layout transform.

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

## Committed this session — review-follow-ups + sync-card fix + nav/UX + review-fixes

**Committed on top of `56d8689` as three commits:** `b6ae660` `refactor(core):` (single-source detail skip +
recycle shard pruning), `fa1e20f` `fix(ui):` (primary-click filtering on cards + transparent-GIF tile ghosting),
`ff3798c` `feat:` (mouse back/forward nav + Import rename + sync-aware Library cards + BoardCardEditor + the
review bug-fixes). The user runtime-verified the nav, click-type, and GIF behaviour across the session.

**An `xhigh` multi-agent code-review** (10 finder angles → verify → sweep) was run over this whole batch and **8
confirmed bugs fixed:** (1) **import-overlap guard made symmetric** — `CanImport`/`ImportAsync` now also block on
the shared `ImportStatus.IsImporting` (a board Sync no longer leaves the Library Import button live to clobber it);
(2) **mouse back/forward swallowed while a modal sheet is open** (`MainWindow` checks for any open `SheetHost`) so
the gesture can't tear the page out from under an overlay; (3) **forward thunks return null when their project was
deleted** (the `_forward` thunk type is now `Func<ViewModelBase?>`, `GoForward` drops a dead entry) so forward
can't rebuild a blank Library for a gone project; (4) **`RefetchTile` re-checks cancellation after its await** (the
completed-just-before-dispose race); (5) **the Library Edit popup re-binds to the rebuilt card** when an external
sync refreshes the grid under it; (6) **`LibraryViewModel.ImportAsync` is dispose-cancellable** (a `_disposeCts`
like Board's) so backing out mid-import doesn't toast/rebuild on the dead VM; (7) **`BoardViewModel.Dispose`
**Cancel**s (not just Disposes) the search/folder-reload debounces** so a mid-`Task.Delay` reload bails; (8) **`Pop`
records the forward thunk before assigning `Current`** so the `CanGoForward` notification isn't stale. One review
finding (off-thread `Tiles` mutation crash) was judged a **false positive** — the caller's `await` resumes on the
UI `SynchronizationContext`, and the identical committed Board-watcher proves it; two **altitude** notes (the
`ShowBoard` thunk reading the mutable `_thumbnails` field, the `_selfImporting` ordering dependence) were left as-is.

**Input — primary-click filtering (user-reported).** The custom tappable cards (`BoardCard`/`ItemCard`/`NewCard`/
`NewProjectCard`) opened on **any** mouse button because Avalonia's `Tapped` gesture fires for left/right/middle/
thumb alike; they now track `IsLeftButtonPressed` from `PointerPressed` and only activate on a primary click (the
`SheetHost` scrim's click-away dismiss filters the same way). `Button`/`ListBox` already primary-filter.

**GIF tile transparency (user-reported).** A playing GIF tile kept its **still thumbnail painted behind** the
`AnimatedImageControl`, so a transparent GIF's see-through regions ghosted the static frame as onion-skin
artifacts (the detail screen was fine — there the still and the player are mutually exclusive). `ItemCard` now
**hides the still thumbnail while `PlaySource` is set**, so transparent areas show the card background instead.

**UX — browser-style mouse back/forward + Import rename.** The mouse thumb buttons (XButton1/XButton2) now drive
back/forward: `NavigationService` gained a **forward stack** (`GoForward`/`CanGoForward`) that holds only a
rebuild *thunk* per backed-out page (the popped VM is still disposed — no kept-alive board/tiles), cleared on a
fresh `Push`; `MainWindow` routes the buttons to `MainWindowViewModel.NavigateBack/Forward`, where back runs the
current page's own `IHasBack.BackCommand` and forward rebuilds the page fresh. **The whole Projects → Library →
Board chain is now one back/forward stack** — `Reset` only builds the root launcher at startup; the Library's
back **pops to the launcher** (made `IResumable`, reloads recents on reveal) instead of resetting, and
`ShowLibrary` records a rebuild thunk — so **forward re-enters a project after you back out to Projects** (the
reported bug). A forward thunk always rebuilds for the currently-open project (changing `ProjectManager.Current`
goes through `Push`, which clears forward). The Library grid's leading **"New board" card is renamed "Import"**
(it opens the import sheet).

**Bug fix — the Library card now reflects a board Sync.** Syncing a board (from the Board screen) drove only the
shared `ImportStatus`, which `LibraryViewModel` never observed — so unlike an import, the board's **card in the
Library showed no progress strip / live count**. `LibraryViewModel` now **subscribes to `ImportStatus`** (and is
`IDisposable`, unsubscribed on nav dispose) and mirrors it onto the matching `BoardCardRef`, so an import *or* a
sync lights the card up. A `_selfImporting` guard stops the watcher double-refreshing (or racing the failed-import
discard) for the grid's own imports; `RefreshAsync` seeds a rebuilt card's strip from `ImportStatus` so returning
to the Library mid-sync doesn't drop it.

Otherwise internal cleanup, **no user-facing feature change.** **New file:** `Desktop/ViewModels/BoardCardEditor.cs` — the
shared rename / clear-cache / delete lifecycle behind a `BoardCardRef`'s Edit popup, replacing the near-identical
board-edit (in `LibraryViewModel`) and folder-edit (in `BoardViewModel`) blocks. Each VM constructs one with its
own noun ("board"/"folder"), card-removal callback, optional post-change hook (folder re-evaluates `CanMoveSelected`),
and optional `loadExtraDetail` hook (the board edit lists merged sources; the folder edit passes none).
**Modified — Desktop:** `LibraryViewModel`/`BoardViewModel` (drop the duplicated edit blocks → `BoardEditor`/`FolderEditor`),
`Views/LibraryView.{axaml,axaml.cs}` + `Views/BoardView.{axaml,axaml.cs}` (bind the sheets to `*Editor.*`),
`AssetTileViewModel` (missing-file `File.Exists` probe now off the realising thread), `BoardViewModel` (`SyncAsync`
refuses to start while `ImportStatus.IsImporting`; a dispose-linked `CancellationTokenSource` cancels an in-flight
`RefetchTile` so navigating away can't mutate the dead VM). **Modified — Core:** `Library/LibraryService.GetBoardDetailAsync`
(skip the per-source count query unless ≥2 sources); `Storage/IMediaStore` + `ContentAddressedStore` +
`ProjectMediaStore` (new `PruneEmptyShards`) + `Library/CurationService.FreeBlobsAsync` (prune empty shard dirs
after the batched recycle, matching `DeleteAsync`; `NestedBoardTests` asserts it). + docs (`CLAUDE.md`, this
file). **Tests: 95 (Core) / 29 (Desktop) green; build clean.**

**Before commit — runtime-verify** (close the app first): **mouse back/forward (thumb buttons) navigate the
Projects ↔ Library ↔ Board ↔ folder stack** — back matches each ← button (Board pops, Library → Projects), forward
returns to a backed-out board *and re-enters a project after backing out to Projects* (rebuilt fresh), and opening
a different project from Projects clears the forward history; the Library's leading card reads **"Import"** and opens the import
sheet; **sync a board → its Library card shows a progress strip + live count and refreshes its total when done**
(matching an import); board pencil (Library) and folder pencil
(Board screen) both rename / clear-cache / delete and show the right counts/sources; deleting a board/folder drops
its card and recycles its images; a folder card's pencil still re-enables/disables the detail-panel Move after
delete; Sync is blocked with a toast while an import runs. **Review-fix checks:** the thumb back/forward buttons do
nothing while a sheet (import/edit/confirm/sync/move) is open; the Library Import button is disabled while a board
Sync runs; deleting the open project from Projects then pressing forward does nothing (no blank Library).
**Click-type check:** right / middle / mouse-4-5 clicking a card (project/board/folder/tile, "+ New", "Import")
does **not** open or activate it — only a left click does. **GIF check:** play a transparent-background GIF tile
in the grid — its see-through regions show the card background, with no ghost of the still thumbnail behind it.
**Still deferred:** the larger architectural follow-ups (pin-id-keyed link model, an "Unfiled" view for orphaned
assets, `Sha256`-reassign dedup on restore). Section auto-foldering and the small review cleanups are done.

## This session — shell chrome (breadcrumb + floating bar + contextual search) + Settings, user-verified

The per-page top bars are **gone**, replaced by shell chrome (see the CLAUDE.md bullets for the full mechanics):

- **Thin breadcrumb strip** (34px, top): the nav trail (`Projects › project › board › folder`), ancestors
  clickable (`NavigationService.BackTo` over the new `PageChain`), current crumb plain and carrying a **live
  search-result count** ("Terrain Ideas (2 folders · 12 items found)"). Overflow crops from the BASE end via
  the pure, unit-tested `BreadcrumbTrimmer` ("…est Backup › Terrain Ideas › Buildings").
- **Floating bottom bar** (Pinterest-style pill; `RadiusXl` = buttons' `RadiusLg` scaled to the bar, concentric
  ＋-menu card): **← back** · **🔍 search** (the pill morphs into the input; Ctrl+F opens it; contextual scope —
  board images + folder names / Library board names / launcher recents, all hidden-not-removed filters; the
  "All images" card is exempt) · **＋** (context menu: New project / Import board / Sync + New folder) ·
  **⚙ Settings**. The bar fades out under any sheet (`SheetHost.IsOpenChangedEvent` → `Chrome.IsModalOpen`)
  and the lightbox (`IImmersivePage`). Pages feed the chrome via the `PageChrome` contracts
  (`ICrumbTitled`/`IProvidesSearch`/`IProvidesPlusActions`), tested against fakes. `IHasBack` + the ← chevrons
  are deleted.
- **Settings** (`UiSettingsStore` → `%APPDATA%/Hoard/ui-settings.json`, its own file so `ProjectManager`'s
  `settings.json` rewrites can't clobber it): theme (applies live + at startup), **interface scale 75–150%**
  (a layout transform over the whole shell — screens reflow, masonry re-columns), default cookies browser
  (pre-selects in the import/sync sheets), **GIF autoplay + max-playing budget** (the old hardcoded LRU of 12).
  Autoplay is **viewport-driven** (debounced scan in `BoardView`), NOT realization-driven — playing on
  `ElementPrepared` let the repeater's off-screen realization buffer evict every visible GIF from the LRU.
  `AnimatedImageControl` hardening: re-attach reloads a source-but-frameless control (recycled elements),
  detach can't strand `IsLoading`, and the tile's still thumbnail stays under the loading bar until frames
  actually render (`HasFrames`). Gallery gained a Shell-chrome section (working breadcrumb/bar/Settings demos).

**Tests: 95 (Core) / 81 (Desktop) green.** New Desktop suites: `BreadcrumbTrimmerTests`,
`ShellChromeViewModelTests`, + `PageChain`/`BackTo` coverage in `NavigationServiceTests`.
