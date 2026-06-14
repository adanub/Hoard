# Design system

How Hoard looks, and how to keep it consistent. **Read this before building any UI.** Token *values* live
in code at `src/Hoard.Desktop/Theme/Tokens.axaml` (the source of truth); this document is the rationale,
the component specs, and the rules.

We build our **own** lightweight Avalonia styles — no third-party UI library. [shadcn/ui](https://ui.shadcn.com)
is the design *reference*, not a dependency (which is shadcn's own philosophy: copy the component, don't
take the dependency).

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
- **Neumorphism.Avalonia** — referenced for **one** thing only: a soft, subtle elevation shadow on raised
  surfaces. We **reject** its heavy bevel / inset / gradient overuse.

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
| `BackgroundBrush` | `#FFFFFF` | `#0A0A0A` | page background |
| `ForegroundBrush` | `#0A0A0A` | `#FAFAFA` | primary text/icons |
| `CardBrush` / `PopoverBrush` | `#FFFFFF` | `#1A1A1A` | surfaces, sheets |
| `MutedBrush` / `AccentBrush` | `#F5F5F5` | `#262626` | subtle fills, hover |
| `MutedForegroundBrush` | `#737373` | `#A3A3A3` | secondary text |
| `LineBrush` / `InputBrush` | `#E5E5E5` | white @10–15% | hairlines, input borders |
| `PrimaryBrush` | `#1A1A1A` | `#E5E5E5` | primary button fill |
| `PrimaryForegroundBrush` | `#FAFAFA` | `#1A1A1A` | text on primary |
| `RingBrush` | `#A3A3A3` | `#737373` | focus ring |
| `DestructiveBrush` | `#DC2626` | `#EF4444` | delete |

- **Radius:** `RadiusSm` 6 · `RadiusMd` **10** (default) · `RadiusLg` 14 · `RadiusFull`.
- **Spacing:** 4 / 8 / 12 / 16 / 24 / 32 (`Space1`…`Space8`).
- **Type:** Inter; `FontSizeBase` **14** (shadcn component default), xs 12 · sm 13 · lg 16 · xl 20 · 2xl 24.
  Headings: semibold, slightly tight tracking. Secondary text uses `MutedForegroundBrush`.
- **Shadow:** `ShadowSm` on raised surfaces only (cards, popovers, sheets); `ShadowMd` for transient
  overlays. Flat list rows get **no** shadow — borders/hover do the work.

## Components (inventory + specs)

Built as Avalonia `Styles` / `ControlThemes` over standard controls; add new ones under `Theme/Controls/`.

- **Button** — variants `default` (primary fill), `secondary` (muted fill), `outline` (border, transparent),
  `ghost` (transparent, muted on hover), `destructive` (red). Sizes `sm` / default / `icon` (square). Radius
  md, visible focus ring. **Replaces** the current ad-hoc `accent` / `danger` / `overlay` styles.
- **Card / Surface** — `CardBrush`, `LineBrush` border, radius md, optional `ShadowSm`.
- **Input** (`TextBox`) — `InputBrush` border, radius md, ring on focus, muted placeholder.
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
