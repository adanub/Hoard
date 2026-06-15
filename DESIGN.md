# Design system

How Hoard looks, and how to keep it consistent. **Read this before building any UI.** Token *values* live
in code at `src/Hoard.Desktop/Theme/Tokens.axaml` (the source of truth); this document is the rationale,
the component specs, and the rules.

We build our **own** lightweight Avalonia styles — no third-party UI library. [shadcn/ui](https://ui.shadcn.com)
is the design *reference*, not a dependency (which is shadcn's own philosophy: copy the component, don't
take the dependency).

**The look blends two things:** shadcn's hard contrast (thin borders, clear fills, strong text, an accent
colour) **and** Neumorphism's tactile depth (dual-tone bevels — a dark shadow plus a light highlight — so
surfaces feel raised and controls press *in*). Depth and contrast each carry half the weight: bevels make it
tactile, borders/fills/accent keep it legible. Base tones are deliberately **not** pure black/white, so the
bevel can throw a highlight *and* a shadow.

## Principles

- **Content-first, minimal.** The media is the interface; chrome recedes. Prefer fewer elements.
- **One job per screen.** Navigate with a back-stack (Projects → Library → Board → Image detail), a back
  chevron to go up a level. No persistent sidebars or piled-on panels.
- **Mobile-first responsive.** Design for the narrowest phone width first, then let the *same* screens reflow
  up to desktop. No separate desktop layout. (Sets up the Phase 4 mobile head for free.)
- **Own our styles.** shadcn/ui is the look; we reimplement it as Avalonia styles over standard controls.
- **Dark-primary.** An image/GIF archive reads best on dark; light is fully supported as the alternate.

## References — what we borrow, what we reject

- **shadcn/ui** (https://ui.shadcn.com) — primary visual language: neutral palette, hairline borders, 10px
  radius, restrained type, subtle shadows, clear focus rings. We reimplement its components; no dependency.
- **Lucide** (https://lucide.dev, **ISC licence**) — icon set (shadcn's). 24×24, stroke-based, stroke-width
  2, round caps/joins. Embedded as Avalonia geometries — **no SVG library**.
- **Inter** — UI typeface, already bundled (`Avalonia.Fonts.Inter`).
- **Neumorphism.Avalonia** — referenced for restrained **soft-UI depth**: a layered "raised" shadow with a
  faint top-edge highlight on surfaces, and an **inset "pressed"** feel on controls. We **reject** its heavy,
  overused bevel / gradient — depth is a subtle cue, never the whole aesthetic.

## Source of truth & file map

- **`Theme/Tokens.axaml`** — **canonical** token values: colours (light + dark via `ThemeDictionaries`),
  radius, spacing, type sizes, shadows. Views bind these with `{DynamicResource ...}` and **never hardcode**
  a colour, radius, or spacing value. Missing a value? Add a token; don't inline.
- `Theme/Icons.axaml` — Lucide icon geometries, added on demand (keyed `Icon.<name>`).
- `Theme/Controls/*.axaml` — one file per component's styles (`Button.axaml`, `Card.axaml`, …).
- `Theme/Theme.axaml` — merges Tokens + Icons + Controls; referenced from `App.axaml` (replaces
  `FluentTheme` when UI work begins).
- **Component gallery** — a dev-only view listing every component, variant, and icon. Our Storybook
  equivalent and the best guard against drift: build it alongside components and keep it current.

## Tokens

Colour roles (values authoritative in `Tokens.axaml`):

| Role (brush key) | Light | Dark | Use |
|---|---|---|---|
| `BackgroundBrush` | `#E8EBF1` | `#17171C` | page background (soft, not pure b/w) |
| `ForegroundBrush` | `#1A1B22` | `#F3F3F7` | primary text/icons |
| `CardBrush` / `PopoverBrush` | `#EDF0F6` | `#1F1F27` | raised surfaces, sheets |
| `MutedBrush` | `#DEE2EB` | `#272730` | subtle fills, hover |
| `MutedForegroundBrush` | `#646876` | `#9A9AA8` | secondary text |
| `LineBrush` / `InputBrush` | black ~10% | white ~15% | thin borders, input borders |
| `PrimaryBrush` | `#5B5BEF` | `#6D6DF5` | **accent** — primary button, accent text |
| `PrimaryForegroundBrush` | `#FFFFFF` | `#FFFFFF` | text on accent |
| `AccentBrush` | accent ~13% | accent ~19% | selected/active surfaces |
| `RingBrush` | `#5B5BEF` | `#8385F7` | focus ring (accent) |
| `DestructiveBrush` | `#DC2626` | `#EF4444` | delete |

The **accent is one token** (`PrimaryBrush` + `AccentBrush` + `RingBrush`) — change the indigo here to recolour the whole UI.

- **Radius:** `RadiusSm` 6 · `RadiusMd` **10** (default) · `RadiusLg` 14 · `RadiusFull`.
- **Spacing:** 4 / 8 / 12 / 16 / 24 / 32 (`Space1`…`Space8`).
- **Type:** Inter; `FontSizeBase` **14** (shadcn component default), xs 12 · sm 13 · lg 16 · xl 20 · 2xl 24.
  Headings: semibold, slightly tight tracking. Secondary text uses `MutedForegroundBrush`.
- **Depth** (tuned per light/dark). The tactile look = a glossy **gradient fill** + **dark-only** shadows:
  - **Gloss fills carry the "light":** `PrimaryGradientBrush` / `SecondaryGradientBrush` /
    `DestructiveGradientBrush` — a bright top sheen → body → darker base gives buttons their glassy 3D look.
  - **Box-shadows are DARK-only — never light.** Avalonia renders a colour fringe (a green hairline) on a
    light-coloured box-shadow over a rounded corner (Avalonia #16010/#10539), so the highlight must come from
    the gradient, not a light shadow. `ButtonRestShadow` = small even drop + soft dark inner base;
    `ButtonPressedShadow` = dark inset (presses in); `ShadowRaised` = soft drop for cards; `ShadowInset` =
    dark inset (recessed inputs); `ShadowMd` = overlays; `ShadowNone` = flat.
  - **`EdgeBrush`** = dark border crisping a coloured button's edge; **`SelectionBrush`** = text selection.
    Keep outer drops small so they never clip.

## Components (inventory + specs)

Built as Avalonia `Styles` / `ControlThemes` over standard controls; add new ones under `Theme/Controls/`.

- **Button** — variants `default` (accent fill), `secondary` (neutral **raised pill** — lighter than the
  page, floats on its bevel), `outline` (border, flat), `ghost` (transparent, flat), `destructive` (red).
  Sizes `sm` / default / `icon`. Solid variants sit raised (`ShadowSm`) and press *in* (`ShadowPressed`);
  flat variants opt out (`ShadowNone`). Thin border, accent focus ring. **Replaces** the old ad-hoc
  `accent` / `danger` / `overlay` styles.
- **Card / Surface** — `CardBrush`, thin `LineBrush` border, radius lg, `ShadowRaised` (dual-tone bevel).
- **Input** (`TextBox`, theme `HoardInput`) — a **recessed** field: `ShadowInset` so it looks pressed into
  the surface, thin border, accent focus ring, muted placeholder.
- **Row** — list item (project / board / collection): transparent, hover `MutedBrush`, radius md, optional
  trailing count or `⋯` action.
- **Badge / Tag** — small; muted or solid. GIF badge, item counts.
- **Separator**, **Sheet / Dialog** (card + `ShadowMd` + scrim), **Toast** (status messages).

## Icons

Lucide, from https://lucide.dev. **To add one:** find the icon → copy its SVG → translate the
path/line/circle/rect elements into a single Avalonia `StreamGeometry` → add it to `Theme/Icons.axaml` keyed
`Icon.<name>` → render via the `Icon` control (a styled `Path`: stroke = `ForegroundBrush`, round caps,
thickness ~1.5–2 scaled to size). Keep a NOTICE for the ISC licence. Initial set: `chevron-left`, `plus`,
`search`, `more-horizontal`, `trash-2`, `rotate-ccw`, `folder`, `folder-plus`, `image`, `settings`,
`download`, `x`.

## Layout & responsive

- **Breakpoints** (window / container width): compact `<600` (phone) · medium `600–1024` (tablet) ·
  expanded `≥1024` (desktop).
- **Techniques** (Avalonia 12, no library): `OnFormFactor` for structural mobile/desktop differences;
  **container queries** (`Selector="Border[Width>=400]"`) for component-level reflow; a breakpoint view-model
  observing window width for live changes; `ItemsRepeater` reflow for the grid (the masonry already does this
  via `MasonryPacker`).
- On expanded widths, centre content at a max width (~1100px). Touch targets ≥40px on compact.

## Navigation

Back-stack, one job per screen:

```
Projects ──▶ Library (boards/collections) ──▶ Board (images) ──▶ Image detail
   │ ⋯              │ import · new collection        ◀── back          ◀── back
[Manage popup]      ◀── back to Projects
```

Project management, import, and new-collection are **sheets/popups**, not persistent chrome.

## Conventions (and for Claude)

- **Never hardcode** a colour, radius, or spacing in a view — reference a token via `{DynamicResource ...}`.
  If the token doesn't exist, add it to `Tokens.axaml`.
- A **new component** gets a style file under `Theme/Controls/` **and** an entry in the gallery; match it
  against the shadcn reference.
- **British/Australian spelling** in UI strings (repo-wide convention).
- Keep it minimal — let the media dominate; resist adding chrome.

## Deliberately not adopting

- **No third-party UI library** (Semi.Avalonia / SukiUI / Material / Fluent-extras). They add bloat and lag
  behind Avalonia 12; we own a small, maintainable style layer instead. (SukiUI is also desktop-only.)
- **No W3C DTCG design-tokens JSON** yet — it would duplicate `Tokens.axaml` with no second consumer.
  Revisit only when the browser extension (TS) or mobile head needs to share tokens across stacks; then
  generate `Tokens.axaml` **and** the TS theme from one JSON.
