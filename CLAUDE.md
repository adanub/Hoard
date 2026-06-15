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
pwsh tools/fetch-gallery-dl.ps1                           # download the bundled gallery-dl.exe (not committed)
```

- **The running app locks its output DLLs**, so a full build fails with `MSB3027` while it's open. Build a
  non-Desktop project (`tests/...` or `src/Hoard.Core`) to iterate, or close the app first.
- **`tools/gallery-dl/gallery-dl.exe` is required at runtime but gitignored** — run the fetch script after a
  clone. The Desktop csproj copies it next to the app output.

## Architecture

Three assemblies, split by *platform reach* — this is the load-bearing decision, keep it intact:

- **`Hoard.Core`** — platform-neutral. Domain, EF Core/SQLite metadata, the SHA-256 content-addressed blob
  store, the project model, ingest + library services, and the `ISourceConnector` abstraction. **Must stay
  free of subprocess / P-Invoke / Avalonia code** so a future mobile head and sync server can reference it.
- **`Hoard.Ingest.GalleryDl`** — desktop/server only. The gallery-dl connector spawns a subprocess (forbidden
  on iOS / restricted on Android), so it lives here, not in Core. Browser-cookie detection lives here too.
- **`Hoard.Desktop`** — Avalonia MVVM app (views, view models, custom controls). Hosts Core in-process for now.

Concepts that span multiple files:

- **Storage is project-scoped.** A "project" is a user-chosen folder (`HoardProject`) holding `store/`,
  `hoard.db`, `thumbnails/`, `logs/`, `download-archive.db`. `ProjectManager.Current` is the single source of
  truth; `ProjectDbContextFactory` and `ProjectMediaStore` read it, so opening/switching a project re-points
  all storage with no other wiring. Only app settings + the global log live under `%APPDATA%/Hoard`. SQLite
  uses **WAL** and **`Pooling=False`** (so an idle project folder holds no file lock and stays movable/deletable).
- **Schema versioning is additive, via `PRAGMA user_version` — not EF migrations.** `EnsureCreated` builds a
  fresh project DB from the full current model; an existing DB (maybe from an older app version) is patched by
  `Metadata/SchemaInitializer.cs`, which applies the additive DDL upgrades it predates and stamps `user_version`.
  Each user owns many independent project DBs, so this is lighter than migrations. **When you change the model:
  bump `SchemaInitializer.LatestSchemaVersion` and add a matching idempotent `CREATE … IF NOT EXISTS` upgrade
  whose DDL matches what EF would generate** (verify with `db.Database.GenerateCreateScript()`). Non-additive
  changes (renames/drops) would need a real migration — cross that bridge if it ever comes up.
- **Ingest is a stream, not a batch.** `ISourceConnector.DownloadAsync` takes an `onItem` callback and invokes
  it per item as it lands; `IngestService` stores + upserts per item and reports the new `AssetView` via
  `IngestProgress.ImportedAsset`, which the library VM appends to the grid live. Dedup happens twice: gallery-dl's
  `--download-archive` skips already-fetched pins *before* download, and the content-addressed store dedups by
  hash *after*.
- **The sync log is append-only and content-keyed.** Every add (`IngestService`, on a genuinely new asset)
  and remove (`CurationService.DeleteAssetAsync`) writes a `SyncOp` via `Sync/SyncLog.cs`, in the *same*
  `SaveChanges` as the change so the history can't drift. Ops are keyed by the asset's SHA-256, not its local
  row id, so they replay on another device that holds the same content under a different id. This is the
  Phase 3 (cloud sync) foundation; nothing reads it yet — keep it append-only (never mutate/delete ops).
- **Shell navigation.** `MainWindowViewModel` swaps a single `CurrentPage` between `ProjectLauncherViewModel`
  and `LibraryViewModel`; the template `ViewLocator` maps each page VM to its `…View`.
- **Decoded GIF frames are reference-counted, not cached-with-retention.** `RefCountedCache<T>` (in
  `Infrastructure/`) does single-flight load per key and hands out disposable `ResourceLease<T>` handles; frames
  are freed when the **last** lease is disposed. `AnimatedImageControl` holds exactly one lease and disposes it
  in one place. Memory tracks what's on screen — do not reintroduce a sticky/LRU GIF cache. (This replaced an
  earlier design whose scattered release logic caused a series of leaks.)
- **Masonry layout** splits the pure packing/visibility math (`MasonryPacker`, Avalonia-free, unit-tested,
  per-column binary search) from the `VirtualizingLayout` shell (`MasonryLayout`). Keep new layout math in the
  packer so it stays testable.
- **Logging.** Serilog → the launching terminal (the WinExe attaches to the parent console) **and**
  `%APPDATA%/Hoard/logs/hoard.log`. Per-import transcripts are written into the project's `logs/`. Read the log
  file directly to diagnose failures rather than asking the user to copy from the UI.

## Conventions

- **Don't commit until it's verified and asked for.** A green build + passing unit tests is **not** enough to
  commit — runtime behaviour (GUI, GIF playback, delete, memory) can only be confirmed by running the app,
  which only the user can do here. Implement → build → test → hand to the user to verify → **wait for them to
  confirm and explicitly ask** before committing. Until then, leave changes uncommitted in the working tree.
- **Commits: Conventional Commits** — `feat:`, `fix:`, `docs:`, `refactor:`, `perf:`, `chore:`, with optional
  scope and a `(wip)` marker for in-progress work (e.g. `perf(wip): …`). End commit messages with the
  `Co-Authored-By: Claude …` trailer.
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
- The token layer exists but isn't wired into `App.axaml` yet (still `FluentTheme`); the existing
  `accent`/`danger`/`overlay` button styles are placeholders the token-driven Button variants will replace.
- **Art/material assets follow `ASSETS.md`.** Committed + embedded under
  `src/Hoard.Desktop/Assets/Materials/<Name>/`, each with a data-driven `material.json` manifest (no hardcoded
  file names). Material/shader rendering code lives in `Hoard.Desktop/Rendering/` (Skia-specific; Core stays neutral).

## Working in this repo (environment realities)

- **You cannot launch the GUI here.** Build + tests verify logic and that things compile/lay out; runtime
  behaviour (scroll feel, GIF playback, memory) must be confirmed by the user — point them at `hoard.log`.
- **The sandbox may block executing the bundled `gallery-dl.exe`** (third-party binary), including via the test
  runner. Direct diagnostic runs are sometimes allowed; don't tunnel execution through tests to get around a block.
- **Avalonia 12 gotchas already hit:** `ItemsRepeater` is a separate NuGet package; the clipboard API changed to
  `DataTransfer`/`DataFormat` (a read-only `TextBox`'s built-in `Copy()` sidesteps it); Fluent `Button`
  backgrounds live on the template's `ContentPresenter`, so style `Button.<class> /template/ ContentPresenter#PART_ContentPresenter`
  across states (`:pointerover`/`:pressed`/`:disabled`) rather than setting `Background` directly — see the
  `overlay`/`danger` button styles. **Grid order is by Pinterest pin id (`SourceId`), descending** —
  `LibraryService` fetches in `Id` order then sorts in memory by the numeric pin id, so order is deterministic
  and survives re-import/restore (`Id` is the stable tiebreak; pinless rows sort last). The sidecar carries
  **no per-pin date** (`CreatedAt` is null for Pinterest — only board-level timestamps exist), and SQLite
  can't `ORDER BY` the id as a number anyway; page-aware sorting would need a stored numeric sort key.
