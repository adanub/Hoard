# Hoard — self-hosted media archive (working name)

A locally-owned, cross-platform archive of media saved from the web. Pinterest is the first
ingestion *connector*; the architecture treats sources as pluggable so more can be added behind the
same contract. See the design notes in `~/.claude/plans/i-d-like-to-create-ethereal-wall.md`.

This repo is **Phase 1**: a .NET 10 / Avalonia desktop app that imports a Pinterest board into a
local content-addressed archive with metadata, and browses it.

## Architecture

```
Hoard.Core (class library)              ← platform-neutral; safe for desktop, server, AND mobile
  Connectors/ISourceConnector           ← pluggable source abstraction (no impl)
  Storage/ContentAddressedStore         ← SHA-256 sharded blob store on disk (free dedup)
  Metadata/HoardDbContext               ← SQLite via EF Core
  Projects/                             ← project model, manager, project-scoped DB/store
  Ingest/IngestService                  ← orchestrates download → store → upsert assets/boards/tags
  Library/LibraryService                ← read-side queries for the UI
Hoard.Ingest.GalleryDl (class library)  ← DESKTOP/SERVER only — spawns gallery-dl, parses sidecars,
                                          browser-cookie detection (subprocess → not usable on mobile)
Hoard.Desktop (Avalonia, MVVM)          ← shell + launcher/library views; hosts Core in-process
tools/gallery-dl/gallery-dl.exe         ← bundled downloader (copied to app output)
```

`Hoard.Core` deliberately has **no subprocess or platform-specific code**, so a future mobile head
can reference it directly. gallery-dl ingestion lives in `Hoard.Ingest.GalleryDl` because spawning a
subprocess is impossible on iOS / restricted on Android — mobile reaches ingestion through the
planned `Hoard.Server` (which will reference Core + the same ingestion assembly) instead.

## Prerequisites

- .NET 10 SDK
- `tools/gallery-dl/gallery-dl.exe` — fetch with `pwsh tools/fetch-gallery-dl.ps1`
  (downloads from <https://github.com/gdl-org/builds>, the binary channel referenced by gallery-dl's
  install docs). Verify it before first use: `& tools/gallery-dl/gallery-dl.exe --version`.

## Build & test

```pwsh
dotnet build Hoard.slnx
dotnet test  tests/Hoard.Core.Tests/Hoard.Core.Tests.csproj
dotnet run  --project src/Hoard.Desktop
```

## Projects (where your data lives)

A **project** is a folder *you* choose; all of one archive's data lives inside it and travels with it:

```
<YourChosenFolder>/
  hoard.project.json   # marker: project name + stable id + archive format version
  store/               # the images (content-addressed blobs)
  ops/                 # the archive's history — append-only op log, one file per computer
```

Everything in the folder is static: immutable images plus an append-only history of every change.
Each computer keeps its own fast metadata index (SQLite) under its app data — alongside its own
regenerable caches (thumbnails, per-import logs, gallery-dl's fetched-pins record) — all rebuilt from
the archive, which is why the same project folder can sit on a NAS or synced drive and be opened from
several computers. Projects created by older versions still hold a `hoard.db` inside the folder;
opening one offers a one-time storage upgrade (a `hoard.db.pre-v2.bak` backup stays in the folder).

**Backing up / syncing between computers:** inside a project, ＋ → **Backup** points the archive at
another folder (a backup drive, a NAS share, a synced folder) and **Sync now** reconciles the two —
it first takes changes made elsewhere, then sends yours. A sync costs what *changed*, not what the
archive holds: the history file already records every image, so Hoard moves exactly the new ones instead
of re-checking thousands of files each time. **Repair backup** is the thorough pass — use it if the
backup folder has been altered from outside (files deleted, a drive half-restored); it re-checks every
file on both sides. Two folders carrying the same project marker are copies of the *same* archive, so
this is also how a local working copy and a NAS copy stay in step (and how a machine repairs images
whose files went missing — they're fetched from the backup rather than re-downloaded).

**Getting your images out as ordinary files:** ＋ → **Export project** writes the whole project to a
folder you choose as `<Project>/<Board>/<Folder>/<Title [pin id]>.jpg` — plain folders and files, no
Hoard needed to read them. A board's own ＋ → **Export** does just that board. Names are stable, so
re-running an export only copies what's new.

On launch you get a **project launcher**: pick a recent project, **Open existing folder…**, or create
a **New project** (just type a name + choose a parent location — Hoard creates the folder for you).
Select a recent project to **Remove from list** (forget it, files untouched) or **Delete from disk…**
(permanent, with confirmation — guarded to only ever delete a real Hoard project folder). Inside a
project, **Switch project** returns to the launcher. `%APPDATA%/Hoard` holds only app settings, the
diagnostic log, and each project's rebuildable per-computer state (index + caches) — your images and
their history never live there.

The per-computer metadata index is **SQLite** (in WAL mode on a local disk, so background imports and
browsing don't block each other) — while the archive itself stays plain, portable files. Full-text
search (FTS5) and optional vector search (`sqlite-vec`) stay available without leaving the embedded
model.

## Logs & debugging

Every run logs to **both** the terminal and a rolling file, so you never have to copy errors out of the UI:

- **Live in your terminal:** launch with `dotnet run --project src/Hoard.Desktop` from a terminal — the
  app attaches to that console and streams logs (incl. gallery-dl's own output) as you click around.
- **On disk:** `%APPDATA%/Hoard/logs/hoard.log` (rolls daily, keeps 7 days). Tail it in a second
  terminal while you use the app:
  ```pwsh
  Get-Content $env:APPDATA\Hoard\logs\hoard.log -Wait -Tail 50
  ```
- Each import also writes a self-contained `import-<timestamp>.log` under this computer's project
  state (`%APPDATA%/Hoard/projects/<projectId>/logs/`).

## Using it

1. Launch the app; on the **project launcher** create a new project (name + location) or open a recent one.
2. For your own/private boards, pick the browser you're logged into Pinterest with in the
   **Cookies** dropdown. Firefox-based browsers (Firefox, **Zen**, LibreWolf, Floorp, Waterfox) are
   detected by profile; Chromium-based ones too. Public boards need no cookies.
3. Paste a board / section / pin URL and click **Import**. Progress shows in the status bar.
4. Browse imported images; click a board on the left to filter; **search** by title/description/tags
   (scoped to the selected board); click a tile for metadata.
   Re-importing a board is **incremental**: gallery-dl skips pins already recorded in the project's
   `download-archive.db` (no re-download), and content-hash dedup is a second layer that also catches
   the same image saved under different pins/boards.

## Notes / known limitations

- gallery-dl breaks when Pinterest changes their site — re-run `fetch-gallery-dl.ps1` to update.
- The auto-generated Pinterest "All Pins" board can fail to enumerate; import real boards/sections.
- The grid is a virtualizing **masonry** layout (`Controls/MasonryLayout.cs`) — tile heights come from each
  pin's stored dimensions, so only on-screen tiles are realized. Tag filtering and FTS5 are still pending.
- **GIFs** animate in the detail panel (custom SkiaSharp player, `Controls/AnimatedImageControl.cs`); the grid
  shows their static first frame. **Videos** show metadata + an "Open file" button (external player); in-grid
  video posters (ffmpeg) and grid hover-to-play GIFs are not done yet.
