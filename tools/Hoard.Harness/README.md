# Hoard.Harness — headless render harness

Renders a Hoard page **at any client size, with no display**, writing a PNG and a geometry report per size.
Dev-only: it is not in the release workflow's publish and ships nothing.

```bash
# the default: a board at 1100x700
dotnet run --project tools/Hoard.Harness

# a resize: ONE window, laid out at each size in turn
dotnet run --project tools/Hoard.Harness -- --size 1100x700 --size 1512x945 --size 800x600

# can you actually reach the last row?
dotnet run --project tools/Hoard.Harness -- --items 60 --folders 4 --scroll bottom
```

Each size produces `<out>/board-<W>x<H>.png` and a matching `.txt` holding the same
`[probe]` report the running app writes (`Infrastructure/LayoutProbe.cs`), so both tools speak one format.

`--help` lists every switch.

## Why it exists

Almost every UI bug this project has hit has been a **layout** bug — a wrong scroll extent, a cropped edge, a
panel that doesn't reflow — and layout bugs are decidable from geometry. The harness boots the real theme, the
real views and the same Avalonia the app builds against, so its numbers are the app's numbers, and it needs
neither a GUI session nor a human.

It earned its keep immediately. The "pins cut off at the bottom of a board" bug reads straight off one line —
content 2635px tall in a viewport whose extent stopped at 2423:

```
GridScroll  extent=1090×2423 viewport=1090×700 offset=0,1723 padding=0,10,0,96   # broken
GridScroll  extent=1090×2635 viewport=1090×700 offset=0,1935 padding=0,0,0,0     # fixed
```

## What it does NOT cover

The **platform windowing layer**. Native fullscreen, DPI/scaling changes, and compositor behaviour live in
Avalonia's macOS/Windows backends; a headless window has none of them, and `renderScaling` here is always 1.
A bug that only appears when a real window resizes is out of scope — use the in-app probe for that:

```bash
HOARD_LAYOUT_PROBE=1 dotnet run --project src/Hoard.Desktop
```

which dumps the same report on every resize, on lightbox open/close, and on demand via **Ctrl/⌘+Shift+L**, to
the terminal and to `hoard.log`. The two together are the split: the harness says whether the *layout* is
right, the probe says whether the *window* agrees.

## Fixtures

`Fixtures.cs` builds a board from real generated PNGs in the temp folder (the grid probes and decodes its
files, so fake paths would render a wall of "file missing" tiles). View models come from the real public
constructors with null services — every async load in `BoardViewModel` guards on `_library is null`, which is
the same supported state the XAML previewer uses.

Adding a page: give `Program.BuildPage` another case. Anything reachable from a view model you can construct
without a database is a candidate.
