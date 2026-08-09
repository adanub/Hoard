<div align="center">

<img src="assets/icon/hoard-128.png" width="96" alt="">

# Hoard

**Back up your Pinterest boards locally, on your own disk.**

I often found myself wanting a way to keep things I'd saved on Pinterest downloaded locally - this
was already technically possible by downloading the pins manually, but that didn't seem very fun...

So I looked around if there were any tools out there that already existed for doing this,
and it was surprisingly barren. The only thing out there was **[gallery-dl](https://github.com/mikf/gallery-dl)**,
but using a CLI tool for an otherwise art/design oriented experience didn't quite feel right. I also
coincidentally had been experimenting with trying agentic workflows for creating useful side projects
and tools, so I started hacking away at this problem in my spare time - and Hoard was born 🙂

Hoard downloads the boards you've saved — the full-size images, the titles, the folder structure —
into a plain folder you own, and lets you browse them locally, all through a nice-looking performant
desktop app.

[![CI](https://github.com/adanub/Hoard/actions/workflows/ci.yml/badge.svg)](https://github.com/adanub/Hoard/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/adanub/Hoard?display_name=tag&sort=semver)](https://github.com/adanub/Hoard/releases/latest)

</div>

---

## What it does

|  | |
| --- | --- |
| **Imports a board** | Paste a board, section or pin URL. Private boards work — pick the browser you're logged into and Hoard borrows its cookies. |
| **Keeps folders as folders** | Pinterest sections become nested folders, automatically, to any depth. |
| **Syncs cheaply** | A sync walks a board newest-first and stops as soon as it hits images you already have, so picking up 3 new pins costs a page or two rather than a re-crawl of the whole board. |
| **Merges boards** | Point several Pinterest boards at one local board; they gather into the same grid. |
| **Backs itself up** | Point a project at a second folder — an external drive, a NAS share, a synced folder — and reconcile the two. Only the changes move. |
| **Exports plain files** | Write the whole project out as `Project/Board/Folder/Title [pin id].jpg`. No Hoard needed to read it. |
| **Browses fast** | Virtualised masonry grid, live search, an inline detail band, and a zoom/pan lightbox. GIFs animate in the grid, capped so memory stays flat. |

## Install

Grab the latest build from [**Releases**](https://github.com/adanub/Hoard/releases/latest):

| Platform | Asset |
| --- | --- |
| Windows (x64) | `Hoard-<version>-win-x64.zip` |
| macOS (Apple Silicon) | `Hoard-<version>-osx-arm64.zip` |

Both are self-contained — no .NET install needed — and ship with the downloader bundled.

> **macOS:** the app is ad-hoc signed, not notarised, so Gatekeeper will object the first time.
> Right-click → Open, or `xattr -dr com.apple.quarantine /Applications/Hoard.app`.
>
> **Intel Macs aren't built.** Apple Silicon only.

## Quick start

1. **Make a project.** On launch you get the project launcher: give it a name, choose where the folder
   goes. That folder is your archive.
2. **Import a board.** ＋ → **Import board**, paste the URL. For your own or secret boards, pick the
   browser you're signed into Pinterest with — Firefox, Zen, LibreWolf, Floorp, Waterfox and the
   Chromium family are all detected. Public boards need no cookies.
3. **Wait.** Progress streams into the board card and the grid fills in live as pins land.
4. **Later, sync.** Open the board, ＋ → **Sync board**. It stops early once it reaches pins you already have.
   From the board grid, ＋ → **Sync all boards** does every board in the project in turn; one failing
   board doesn't stop the rest.

**Full sync** (in the same sheet) is the exhaustive version. Use it when a board has gained a *section*
since your last crawl, when the board isn't sorted newest-first, or to re-fetch a file that's gone
missing from disk — a quick sync structurally can't see those.

## Where your data lives

A **project** is a folder you choose. Everything durable lives in it, and it travels:

```
YourFolder/
  hoard.project.json   # marker: name, a stable id, the archive format version
  store/               # your images, content-addressed (identical bytes stored once)
  ops/                 # append-only history — one file per computer
```

That's the whole archive: immutable images, plus a log of every change ever made. Nothing else in there
is precious.

Each computer keeps its own **index** — a SQLite database, thumbnails, logs — under `%APPDATA%\Hoard` on
Windows and `~/.config/Hoard` on macOS. The index is derived: delete it and the next open
rebuilds it from `ops/`. That's deliberate, and it's what lets the same project folder sit on a NAS and
be opened from several machines without a database ever being written over the network.

<details>
<summary><b>Backing up, and using two computers</b></summary>

Inside a project, ＋ → **Backup** points the archive at a second folder and **Sync now** reconciles them:
it takes changes made elsewhere first, then sends yours.

A sync costs what *changed*, not what the archive holds. The history file already records every image, so
Hoard moves exactly the new blobs instead of comparing thousands of files — which is the difference
between a couple of seconds and ten minutes over SMB.

**Repair backup** is the thorough pass. Use it if the backup folder has been altered from outside — files
deleted, a drive half-restored — since a delta can't see damage nothing logged.

Two folders carrying the same project marker are copies of the *same* archive, not rivals. That's the
supported way to keep a local working copy and a NAS copy in step, and it's also how a machine heals
images whose files went missing: they come back from the backup rather than being re-downloaded.

</details>

<details>
<summary><b>Getting your images out</b></summary>

＋ → **Export project** writes everything to a folder you choose as
`<Project>/<Board>/<Folder>/<Title [pin id]>.jpg`. A board's own ＋ → **Export** does just that board.

File names are stable per image, so re-running an export is an incremental refresh — it copies what's new
and leaves the rest alone. The export is read-only against your archive.

</details>

## Build from source

Needs the **.NET 10 SDK**. Nothing else — gallery-dl is fetched by the build.

```bash
dotnet build Hoard.slnx                   # build everything (the solution is .slnx)
dotnet test  Hoard.slnx                   # run the test suite
dotnet run --project src/Hoard.Desktop    # run it (from a terminal, so logs stream)
```

gallery-dl is a ~24 MB third-party binary, too big to commit, so the build downloads the right one for
your OS into `tools/gallery-dl/` the first time. It only fetches when the file is *absent* — to refresh a
copy that Pinterest has broken, run `pwsh tools/fetch-gallery-dl.ps1`. Building offline?
`-p:HoardSkipGalleryDlFetch=true`.

<details>
<summary><b>How the code is laid out</b></summary>

```
src/Hoard.Core              platform-neutral: domain, SQLite metadata, the content-addressed
                            store, the project model, ingest + library services, the op log
src/Hoard.Ingest.GalleryDl  desktop/server only: spawns gallery-dl, parses its sidecars,
                            finds browser cookies
src/Hoard.Desktop           the Avalonia app — views, view models, custom controls
```

The split is by *platform reach*, not by layer. `Hoard.Core` holds no subprocess or platform code, so a
future mobile client or sync server can reference it as-is; anything that shells out lives in
`Hoard.Ingest.GalleryDl`, because iOS can't spawn processes at all.

Deeper notes live in [`CLAUDE.md`](CLAUDE.md) (architecture and the reasoning behind it),
[`SYNC-DESIGN.md`](SYNC-DESIGN.md) (the archive format and replication) and [`DESIGN.md`](DESIGN.md)
(the design system). [`ROADMAP.md`](ROADMAP.md) is what's next.

</details>

## When something goes wrong

Every run logs to the terminal **and** to a file, so you never have to copy an error out of the UI.

- **Live:** launch with `dotnet run --project src/Hoard.Desktop` from a terminal and the app streams its
  log there, including gallery-dl's own output.
- **On disk:** `%APPDATA%/Hoard/logs/hoard.log`, rolled daily, 7 days kept.
  ```pwsh
  Get-Content $env:APPDATA\Hoard\logs\hoard.log -Wait -Tail 50
  ```
- Each import also writes its own transcript under
  `%APPDATA%/Hoard/projects/<projectId>/logs/import-<timestamp>.log`.

If imports start failing across the board, the usual cause is Pinterest having changed something — refresh
gallery-dl with `pwsh tools/fetch-gallery-dl.ps1` before assuming it's Hoard.

## Known limitations

- **Pinterest's auto-generated "All Pins" board** often won't enumerate. Import real boards and sections.
- **Videos** are archived, but tiles show a placeholder rather than a poster frame, and playback opens your
  system player. Poster frames need an ffmpeg dependency that isn't bundled.
- **Search is a `LIKE` scan** over titles, descriptions and tags. Fine for thousands of pins; FTS5 is
  planned for when it isn't.
- **Recycle-bin deletes are Windows-only.** Elsewhere a delete is permanent, and the confirmation says so.
- **Tags are usually empty** — Pinterest's sidecars rarely carry any in practice.
- Interface scale works, but dropdowns and tooltips render in their own popup windows and don't inherit it.

## Credits

- **[gallery-dl](https://github.com/mikf/gallery-dl)** does the actual downloading. Hoard would be a much
  larger project without it. Binaries come from [gdl-org/builds](https://github.com/gdl-org/builds).
- **[Avalonia](https://avaloniaui.net)** for the UI toolkit.
- **[Lucide](https://lucide.dev)** (ISC) for the icons, embedded as geometries.

Hoard is an independent tool. It isn't affiliated with, endorsed by, or connected to Pinterest in any way,
and it only ever fetches content the account you're signed in as can already see.

## Licence

**[GPL-3.0](LICENSE)** — free to use, study, change and share. The condition is that it stays that way:
if you distribute Hoard or a fork of it, you have to ship your source under GPL-3.0 too, and say what you
changed. You can't take it closed.

Hoard bundles the [gallery-dl](https://github.com/mikf/gallery-dl) binary, which is GPL-2.0 and remains
under its own licence — Hoard runs it as a separate program, so the two are merely distributed together.
Third-party components and their licences are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
