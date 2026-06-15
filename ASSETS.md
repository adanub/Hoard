# Art assets

Where image / material assets live and how they're described. Visual/design conventions: `DESIGN.md`; code:
`CLAUDE.md`.

- Assets are **committed** and embedded under `src/Hoard.Desktop/Assets/` via the existing
  `<AvaloniaResource Include="Assets\**"/>` — reached at `avares://Hoard.Desktop/Assets/…`. They're small; no
  special handling.
- **Materials are data-driven:** one folder per material under `Assets/Materials/<Name>/`, described by a
  `material.json` manifest, so loading code never hardcodes file names. Add/swap a material = files only.

## Layout

```
src/Hoard.Desktop/Assets/
  Materials/
    Background/
      albedo.jpg
      normal.jpg
      roughness.jpg
      metalness.jpg
      material.json
```

## Manifest (`material.json`)

```json
{ "name": "Background", "albedo": "albedo.jpg", "normal": "normal.jpg",
  "roughness": "roughness.jpg", "metalness": "metalness.jpg", "tiling": [1, 1] }
```

## Conventions

- Material folders **PascalCase**; map files **canonical lowercase** (`albedo` / `normal` / `roughness` /
  `metalness`). Extend the manifest additively (e.g. add `anisotropy` for brushed metal later).
- Normal maps are ideally PNG (heavy JPEG compression can add faint lighting noise); JPEG is fine if it reads
  clean — re-export to PNG only if a map looks noisy under the light.
- Material/shader **rendering code** (the SkSL shader + the `CompositionCustomVisual` control) lives in
  `Hoard.Desktop/Rendering/`; Core stays platform-neutral.
