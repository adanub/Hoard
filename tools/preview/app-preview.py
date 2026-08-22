#!/usr/bin/env python3
"""Renders the README's app-preview images (assets/preview/app-{dark,light}.svg).

Dev-only, run by hand: `python3 tools/preview/app-preview.py`.

This draws the SAME mock as the landing page's hero (docs/index.html + assets/site.js) — the shell's
34px breadcrumb strip, a masonry clipped mid-scroll, and the floating action bar — down to the tile
colours, because it runs a faithful port of that page's seeded PRNG (mulberry32, seed 97531) over the
same palette and the same column heights. So the README and the site show one board, not two. If you
change the mock there, re-run this.

Why SVG and not a screenshot: the same reasons the README's download badges are hand-built SVGs rather
than shields.io images. It stays crisp at any width, it costs a few KB, it needs no browser to produce,
and it contains nobody's real pins (the repo's "no real personal data" rule) — the tiles are gradients.

Why two files: GitHub picks between them with <picture media="(prefers-color-scheme: ...)">, so the
preview matches the reader's theme. Colours are the app's own tokens, transcribed from
src/Hoard.Desktop/Theme/Tokens.axaml.

Deliberately NO <filter> elements: GitHub sanitises SVG it renders in a README, and a dropped filter
would silently flatten the image. Depth is done with gradient overlays instead.
"""

from pathlib import Path

# ── The landing page's PRNG, ported exactly (verified against the JS output) ──────────────────────
M = 0xFFFFFFFF


def u32(x):
    return x & M


def i32(x):
    x &= M
    return x - 0x100000000 if x >= 0x80000000 else x


def imul(a, b):
    return i32(u32(a) * u32(b))


def rng(seed):
    state = i32(seed)

    def rand():
        nonlocal state
        state = i32(state + 0x6D2B79F5)
        s = state
        t = imul(s ^ (u32(s) >> 15), 1 | s)
        t = i32(i32(t + imul(t ^ (u32(t) >> 7), 61 | t)) ^ t)
        return u32(t ^ (u32(t) >> 14)) / 4294967296

    return rand


# The palette from docs/assets/site.js — [hue, saturation%, lightness%] of each tile's top-left sheen.
PALETTE = [
    (240, 62, 62), (246, 55, 54), (232, 48, 58), (258, 45, 60),
    (240, 20, 46), (225, 16, 38), (268, 34, 52), (212, 38, 50),
    (246, 70, 68), (235, 12, 30),
    (190, 34, 46), (330, 30, 52), (22, 32, 50), (160, 24, 42),
]

# Column tile heights and badge placement, identical to buildMock().
COLUMNS = [
    [150, 96, 210, 128, 170],
    [104, 188, 132, 160, 120],
    [176, 120, 96, 208, 140],
    [128, 164, 184, 108, 150],
    [96, 200, 124, 172, 132],
]
TAGS = {(0, 2): "GIF", (3, 1): "VIDEO", (2, 0): "GIF"}


def hsl_to_hex(h, s, l):
    """CSS hsl() → #rrggbb."""
    h = (h % 360) / 360.0
    s /= 100.0
    l /= 100.0
    if s == 0:
        r = g = b = l
    else:
        q = l * (1 + s) if l < 0.5 else l + s - l * s
        p = 2 * l - q

        def hue(t):
            t %= 1
            if t < 1 / 6:
                return p + (q - p) * 6 * t
            if t < 1 / 2:
                return q
            if t < 2 / 3:
                return p + (q - p) * (2 / 3 - t) * 6
            return p

        r, g, b = hue(h + 1 / 3), hue(h), hue(h - 1 / 3)
    return "#%02X%02X%02X" % tuple(round(v * 255) for v in (r, g, b))


def js_round(x):
    """JS Math.round: half UP, not Python's half-to-even."""
    from math import floor
    return floor(x + 0.5)


def tile_colours(rand):
    """The two stops of one tile's 150deg gradient — tileBackground() in site.js."""
    h0, s, l = PALETTE[int(rand() * len(PALETTE))]
    h = h0 + js_round((rand() - 0.5) * 14)
    return hsl_to_hex(h, s, l), hsl_to_hex(h + 10, max(8, s - 16), max(10, l - 22))


# ── Geometry (mirrors the CSS: 34px strip, 16px body padding, 12px gutters, RadiusLg/Xl) ─────────
W = 1072            # the site's content width at a 1280px viewport
TITLEBAR = 34
PAD = 16
GAP = 12
GRID_H = 430        # .mock__masonry's clamped height
COLS = len(COLUMNS)
COL_W = (W - 2 * PAD - GAP * (COLS - 1)) / COLS
GRID_TOP = TITLEBAR + PAD
H = GRID_TOP + GRID_H + PAD
R_WINDOW, R_TILE, R_KEY = 18, 14, 14
BAR_W, BAR_H = 201, 50

THEMES = {
    "dark": dict(
        bg="#212025", card="#1F1F23", popover="#2C2C33", fg="#F3F3F7", muted_fg="#9A9AA8",
        line="#FFFFFF", line_opacity=".18", sheen="#FFFFFF", sheen_opacity=".12",
        shade="#131219", shade_opacity=".5", chip_a="#35353D", chip_b="#28272E",
    ),
    "light": dict(
        bg="#E4E8EF", card="#F6F8FC", popover="#F8FAFD", fg="#1A1B22", muted_fg="#646876",
        line="#000000", line_opacity=".18", sheen="#FFFFFF", sheen_opacity=".45",
        shade="#000000", shade_opacity=".1", chip_a="#FFFFFF", chip_b="#E7EAF1",
    ),
}

# Lucide geometries, as in Theme/Icons.axaml — drawn in a 24x24 box, scaled to 18.
ICONS = [
    "M15 18 9 12 15 6",                                                    # chevron-left
    "M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0M21 21l-4.3-4.3",                 # search
    "M5 12h14M12 5v14",                                                    # plus
    ("M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0"                                  # settings (gear, simplified
     "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33"
     " 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33"
     "l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0"
     " 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06"
     "a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51"
     " 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65"
     " 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1"),
]


def build(theme_name):
    t = THEMES[theme_name]
    rand = rng(97531)
    defs, body = [], []

    # Tiles, column by column — the same call order as buildMock(), so the colours match the site.
    for c, heights in enumerate(COLUMNS):
        x = PAD + c * (COL_W + GAP)
        y = GRID_TOP
        for r, h in enumerate(heights):
            a, b = tile_colours(rand)
            gid = f"t{c}{r}"
            # 150deg in CSS points down-and-right: direction (sin150, -cos150) = (.5, .866).
            defs.append(
                f'<linearGradient id="{gid}" x1="0.25" y1="0.067" x2="0.75" y2="0.933">'
                f'<stop offset="0" stop-color="{a}"/><stop offset="1" stop-color="{b}"/></linearGradient>'
            )
            body.append(
                f'<rect x="{x:.1f}" y="{y}" width="{COL_W:.1f}" height="{h}" rx="{R_TILE}" fill="url(#{gid})"/>'
                f'<rect x="{x:.1f}" y="{y}" width="{COL_W:.1f}" height="{h}" rx="{R_TILE}" fill="url(#bevel)"/>'
                f'<rect x="{x + .5:.1f}" y="{y + .5}" width="{COL_W - 1:.1f}" height="{h - 1}" rx="{R_TILE - .5}"'
                f' fill="none" stroke="{t["line"]}" stroke-opacity="{t["line_opacity"]}"/>'
            )
            label = TAGS.get((c, r))
            if label:
                tw = len(label) * 5.9 + 14
                body.append(
                    f'<g><rect x="{x + 6:.1f}" y="{y + 6}" width="{tw:.1f}" height="15" rx="7.5"'
                    f' fill="url(#chip)" stroke="{t["line"]}" stroke-opacity="{t["line_opacity"]}"/>'
                    f'<text x="{x + 6 + tw / 2:.1f}" y="{y + 17}" text-anchor="middle" font-size="9"'
                    f' font-weight="600" letter-spacing=".4" fill="{t["fg"]}">{label}</text></g>'
                )
            y += h + GAP

    bar_x = (W - BAR_W) / 2
    bar_y = H - PAD - BAR_H
    keys = [8, 52, 109, 153]
    bar = [
        f'<rect x="{bar_x:.1f}" y="{bar_y}" width="{BAR_W}" height="{BAR_H}" rx="{R_WINDOW}"'
        f' fill="{t["popover"]}" stroke="{t["line"]}" stroke-opacity="{t["line_opacity"]}"/>',
        f'<rect x="{bar_x + 100:.1f}" y="{bar_y + 14}" width="1" height="22" fill="{t["line"]}"'
        f' fill-opacity="{t["line_opacity"]}"/>',
    ]
    for i, kx in enumerate(keys):
        cx, cy = bar_x + kx + 20, bar_y + 25
        bar.append(
            f'<g transform="translate({cx - 9:.1f} {cy - 9:.1f}) scale(.75)" fill="none"'
            f' stroke="{t["fg"]}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
            f'<path d="{ICONS[i]}"/></g>'
        )

    dots = "".join(
        f'<circle cx="{17 + i * 16}" cy="{TITLEBAR / 2}" r="5" fill="{t["muted_fg"]}" fill-opacity=".45"/>'
        for i in range(3)
    )

    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}"
     role="img" aria-label="The Hoard app: a breadcrumb trail, a masonry grid of saved pins, and the floating action bar.">
  <title>Hoard — the board screen</title>
  <!-- GENERATED by tools/preview/app-preview.py. Don't hand-edit; change the mock in docs/ and re-run. -->
  <defs>
    {"".join(defs)}
    <linearGradient id="bevel" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="{t["sheen"]}" stop-opacity="{t["sheen_opacity"]}"/>
      <stop offset="0.45" stop-color="{t["sheen"]}" stop-opacity="0"/>
      <stop offset="0.6" stop-color="{t["shade"]}" stop-opacity="0"/>
      <stop offset="1" stop-color="{t["shade"]}" stop-opacity="{t["shade_opacity"]}"/>
    </linearGradient>
    <linearGradient id="chip" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="{t["chip_a"]}"/><stop offset="1" stop-color="{t["chip_b"]}"/>
    </linearGradient>
    <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0.72" stop-color="#FFFFFF"/><stop offset="1" stop-color="#000000"/>
    </linearGradient>
    <mask id="clip"><rect x="0" y="{GRID_TOP}" width="{W}" height="{GRID_H}" fill="url(#fade)"/></mask>
    <clipPath id="window"><rect x="0" y="0" width="{W}" height="{H}" rx="{R_WINDOW}"/></clipPath>
  </defs>

  <g clip-path="url(#window)">
    <rect width="{W}" height="{H}" fill="{t["bg"]}"/>
    <rect width="{W}" height="{TITLEBAR}" fill="{t["card"]}"/>
    <line x1="0" y1="{TITLEBAR}" x2="{W}" y2="{TITLEBAR}" stroke="{t["line"]}" stroke-opacity="{t["line_opacity"]}"/>
    {dots}
    <text x="62" y="21" font-size="12" fill="{t["muted_fg"]}"
          font-family="Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Helvetica, Arial, sans-serif">Projects <tspan fill-opacity=".6">›</tspan> Reference <tspan fill-opacity=".6">›</tspan> Terrain ideas <tspan fill-opacity=".6">›</tspan> <tspan fill="{t["fg"]}" font-weight="500">Buildings</tspan></text>

    <g mask="url(#clip)">{"".join(body)}</g>
    <g font-family="Inter, -apple-system, BlinkMacSystemFont, Segoe UI, Helvetica, Arial, sans-serif">{"".join(bar)}</g>
  </g>
  <rect x="0.5" y="0.5" width="{W - 1}" height="{H - 1}" rx="{R_WINDOW - .5}" fill="none"
        stroke="{t["line"]}" stroke-opacity="{t["line_opacity"]}"/>
</svg>
'''


if __name__ == "__main__":
    out = Path(__file__).resolve().parent.parent.parent / "assets" / "preview"
    out.mkdir(parents=True, exist_ok=True)
    for name in THEMES:
        path = out / f"app-{name}.svg"
        path.write_text(build(name), encoding="utf-8")
        print(f"wrote {path.relative_to(path.parents[2])} ({path.stat().st_size / 1024:.1f} KB)")
