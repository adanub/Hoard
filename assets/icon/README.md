# The Hoard mark

A masonry grid — the app's actual layout — with one tile in the accent colour: the pin you saved.

`hoard.svg` is the source of truth. Everything else is generated from it.

| File | Used by |
| --- | --- |
| `hoard.svg` | the source; edit this one |
| `hoard-128.png`, `hoard-256.png`, `hoard-512.png` | README, docs, release pages |
| `../../src/Hoard.Desktop/Assets/hoard.ico` | the Windows `.exe` (`ApplicationIcon`) and the Avalonia window |
| `../../tools/packaging/macos/Hoard.icns` | the macOS `.app` bundle (`CFBundleIconFile`) |

## Constraints it's built to

- **Three colours, no gradients.** `#151521` ground, `#E9E9F2` tiles, `#6D6DF5` accent — the last two
  are `Theme/Tokens.axaml`'s dark-theme `PrimaryBrush` and a cool near-white, so the icon and the app
  agree.
- **Legible at 16px.** Six tiles on a three-column rhythm is the most detail that survives a taskbar
  slot. An earlier variant knocked a "pin" dot out of the accent tile; below 64px it read as a notch
  rather than a dot, so it was dropped.
- **Columns land flush on the grid's bottom edge.** Genuinely ragged masonry looks like a half-loaded
  page when it's 32 pixels wide.
- **Deliberately unlike Pinterest's own mark.** No P, no circle, no red — Hoard is an independent tool,
  not a Pinterest product, and the icon shouldn't imply otherwise.

## Regenerating the raster files

The `.ico` carries 16/24/32/48/64 as uncompressed DIBs and 128/256 as PNG; the `.icns` carries
`icp4`/`icp5`/`ic11`/`ic12`/`ic07`/`ic08`/`ic13`/`ic09`/`ic14`/`ic10`, all PNG. With
[ImageMagick](https://imagemagick.org) and, on macOS, `iconutil`:

```bash
for s in 16 24 32 48 64 128 256 512 1024; do
  magick -background none assets/icon/hoard.svg -resize ${s}x${s} /tmp/hoard-$s.png
done

magick /tmp/hoard-{16,24,32,48,64,128,256}.png src/Hoard.Desktop/Assets/hoard.ico

mkdir -p /tmp/Hoard.iconset
for s in 16 32 128 256 512; do cp /tmp/hoard-$s.png /tmp/Hoard.iconset/icon_${s}x${s}.png; done
for s in 16 32 128 256 512; do cp /tmp/hoard-$((s*2)).png /tmp/Hoard.iconset/icon_${s}x${s}@2x.png; done
iconutil -c icns /tmp/Hoard.iconset -o tools/packaging/macos/Hoard.icns
```
