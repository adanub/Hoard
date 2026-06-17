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
- **Mobile-first, fluid reflow (Pinterest-style).** Screens lay out in real pixels and scroll naturally; the
  *same* screens reflow from the narrowest phone up to desktop — the grid gains columns as the window widens.
  No global scaling, no separate desktop layout. (Sets up the Phase 4 mobile head for free.) See **Layout &
  responsive** below.
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
- `Theme/Controls/*.axaml` — component ControlThemes (`Button`, `Input`, `Switch`, `ListItem`), the shared
  `Pressable` template they reuse (one `Border#root` press-surface scaffold), and class-based `Surfaces`
  (Card / Badge / text helpers). Tappable controls reference `PressableSurfaceTemplate` rather than re-declaring it.
- `Theme/Theme.axaml` — merges Tokens + Icons + Controls; referenced from `App.axaml` (replaces
  `FluentTheme` when UI work begins).
- **Component gallery** — a dev-only view listing every component, variant, and icon. Our Storybook
  equivalent and the best guard against drift: build it alongside components and keep it current.

## Tokens

**Values are authoritative in `Tokens.axaml`** (light + dark via `ThemeDictionaries`) — this table lists the
*roles* only, so the doc can't drift from the code. The neutral ramp is a soft blue-violet grey (deliberately
not pure black/white) with a single indigo **accent**.

| Role (brush key) | Use |
|---|---|
| `BackgroundBrush` | page background |
| `ForegroundBrush` | primary text / icons |
| `CardBrush` / `PopoverBrush` | raised surfaces, sheets (on dark the card sits ≈ the page, lifted by its shadow + border) |
| `MutedBrush` | subtle fills, hover, placeholder tiles |
| `MutedForegroundBrush` | secondary text |
| `LineBrush` / `InputBrush` | thin borders / input borders (alpha over the surface) |
| `PrimaryBrush` | **accent** — primary button, accent text, focus-ring base |
| `PrimaryForegroundBrush` | white text/icon on an accent or destructive fill |
| `AccentBrush` | accent at low alpha — selected/active surfaces |
| `RingBrush` | focus ring (accent) |
| `DestructiveBrush` / `DestructiveForegroundBrush` | delete actions |
| `ScrimBrush` | modal sheet backdrop (dims the page) |

The **accent is one token** (`PrimaryBrush` + `AccentBrush` + `RingBrush`) — change the indigo in `Tokens.axaml` to recolour the whole UI.

- **Radius:** `RadiusSm` 6 · `RadiusMd` 10 · `RadiusLg` 14 · `RadiusLgTop` (lg on top corners only — card covers) · `RadiusFull`. Buttons/cards use `RadiusLg`, list rows `RadiusMd`, pills/badges `RadiusFull`.
- **Control heights** (the explicit size metric for buttons): `ControlHeight` 38 (default) · `ControlHeightSm` 28 · `ControlHeightLg` 48; the `sm`/`lg` classes set `Height`, the icon button is square at `ControlHeight`.
- **Spacing:** 4 / 8 / 12 / 16 / 24 / 32 (`Space1`…`Space8`).
- **Type:** Inter; `FontSizeBase` **14** (shadcn component default), xs 12 · sm 13 · lg 16 · xl 20 · 2xl 24.
  Headings: semibold, slightly tight tracking. Secondary text uses `MutedForegroundBrush`.
- **Depth** (tuned per light/dark). The tactile look = a glossy **gradient fill** + box-shadows:
  - **Gloss fills carry the "light":** `PrimaryGradientBrush` / `SecondaryGradientBrush` /
    `DestructiveGradientBrush` — a bright top sheen → body → darker base gives buttons their glassy 3D look.
  - **Box-shadows are dark-only** — the highlight/gloss comes from the gradient, not a light box-shadow.
    `ButtonRestShadow` = small even drop + soft dark inner base; `ButtonPressedShadow` = dark inset (presses
    *in*); `ShadowRaised` = soft drop for cards; `ShadowInset` = dark inset (recessed inputs); `ShadowMd` =
    overlays; `ShadowNone` = flat. Keep outer drops small so they never clip.
  - **🔒 RULE — a text/icon shadow, where used, is ALWAYS the inverse luminance of the text.** Dark text →
    light shadow; light text → dark (black) shadow. **Never** same-luminance (light+light / dark+dark) — that's
    blurry/unreadable. **Only apply it where the text sits on a *contrasting* fill** — i.e. white text on a
    coloured button (primary/destructive), which uses the fixed-dark **`OnAccentTextShadowEffect`**. **Do not
    apply it to neutral/near-background fills** (secondary/outline/ghost, list rows): a light emboss is
    invisible on a near-white surface, so it can't be made consistent across themes (it would just add weight
    in dark mode). ⚠️ When a shadow *does* need to flip per theme, swap the *entire* effect via
    `DynamicResource` — `DynamicResource` on a sub-property *inside* a `DropShadowEffect` does **not**
    re-resolve per theme (that was the earlier bug).
  - **`EdgeBrush`** = dark border crisping a coloured button's edge; **`SelectionBrush`** = text selection.

## Components (inventory + specs)

Built as Avalonia `Styles` / `ControlThemes` over standard controls; add new ones under `Theme/Controls/`.

- **Pressable** (`PressableSurfaceTemplate`) — shared scaffold the tappable controls reuse: a `Border#root`
  the press states transform (translate + `scale`) over a short transition, wrapping a **hit-transparent**
  `ContentPresenter` (the fill is the single hover/hit surface). Button + Row reference it via `Template=`.
- **Button** — variants `default` (accent fill), `secondary` (neutral **raised pill**), `outline` (border at
  rest), `ghost` (bare), `destructive` (red). Sizes `sm` / default / `lg` set an explicit `Height`
  (`ControlHeight*`); `icon` is square at `ControlHeight`. Solid variants sit raised (`ButtonRestShadow`) and
  press *in* (`ButtonPressedShadow` + `scale(0.97)`; `ClipToBounds=False`). `ghost`/`outline` are flat at rest
  and **lift into the secondary pill on hover**. Accent focus ring, inverse-luminance text shadow (locked rule
  above). **Replaces** the old `accent` / `danger` / `overlay` styles.
- **Switch** (`ToggleButton`, theme `HoardSwitch`) — a real on/off **slider switch**: a pill track that recolours
  muted→accent with a knob that slides; no inner text (the label sits beside it).
- **Card / Surface** — `CardBrush`, thin `LineBrush` border, radius lg, `ShadowRaised`. The **project-board
  card** has a 3-up Pinterest **collage cover** (rounded top via `RadiusLgTop`, built from the project's cached
  thumbnails; muted tiles fill any gaps) above the name + cache size and a **⋯ manage menu** (open / clear
  cache / remove / delete); the card body opens the project.
- **Input** (`TextBox`, theme `HoardInput`) — a **recessed** field: `ShadowInset`, thin border, accent focus
  ring, muted placeholder.
- **Row** (`ListBoxItem`, theme `HoardListItem`) — tappable list item (project / board / collection): bare at
  rest, hover lifts into the raised pill, press sinks, `:selected` = accent tint. Used via a `ListBox`
  `ItemContainerTheme`; replaces the old `Border.row` class.
- **Badge / Tag** — small **floating** chip (gloss bevel + border + drop shadow): item counts, GIF tag.
- **Sheet** (`Controls/SheetHost.cs`, theme in `Sheet.axaml`) — reusable in-app modal: a `ScrimBrush` backdrop
  dims the page and centres `Content` in a floating card (`ShadowMd`); scrim-click / Esc dismiss. Hosts
  new-project now; import / new-collection later.
- **Toast** (`Services/ToastService.cs` + `Controls/ToastHost.axaml`) — transient, auto-dismissing status
  messages, bottom-right; the host is shell-mounted in `MainWindow` and hit-test-invisible. Error toasts edge
  in `DestructiveBrush`.
- **Separator**, **Dialog window** (`ConfirmDialog`/`MessageDialog` — used for the destructive delete confirm).

## Icons

Lucide, from https://lucide.dev. **To add one:** find the icon → copy its SVG → translate the
path/line/circle/rect elements into a single Avalonia `StreamGeometry` → add it to `Theme/Icons.axaml` keyed
`Icon.<name>` → render via the `Icon` control (a styled `Path`: stroke = `ForegroundBrush`, round caps,
thickness ~1.5–2 scaled to size). Keep a NOTICE for the ISC licence. Initial set: `chevron-left`, `plus`,
`search`, `more-horizontal`, `trash-2`, `rotate-ccw`, `folder`, `folder-plus`, `image`, `settings`,
`download`, `x`.

## Layout & responsive

- **Fluid reflow, real pixels (primary).** No global scaling. Screens lay out in real DIPs inside a
  `ScrollViewer` and reflow by *actual* width — the **masonry grid spans the full window width and gains
  columns as it widens** (columns = width ÷ `MasonryLayout.TargetColumnWidth`, ~200px → 2 on a phone, 6–8 on
  desktop). This is the Pinterest model; the grid was already built for it (`MasonryPacker`).
- **Breakpoints** (window / container width): compact `<600` (phone) · medium `600–1024` (tablet) ·
  expanded `≥1024` (desktop).
- **Techniques** (Avalonia 12, no library): `OnFormFactor` for structural mobile/desktop differences;
  **container queries** (`Selector="Border[Width>=400]"`) for component-level reflow; a breakpoint view-model
  observing window width for live changes; `ItemsRepeater`/masonry reflow for the grid.
- Grid fills the viewport width edge-to-edge (no max-width cap); touch targets ≥40px on compact.

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
