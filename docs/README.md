# The landing page

The site published at <https://adanub.github.io/Hoard/>. Plain static files — no build step, no
generator, no dependencies, and no request leaves the visitor's browser (fonts, icons and images are
all local or inline).

```
docs/
  index.html          the whole page
  assets/styles.css   tokens + components, transcribed from the app's theme
  assets/site.js      the drifting backdrop, the app mock's tiles, theme toggle
  assets/hoard-*.png  copies of assets/icon/ (Pages serves docs/ as the site root, so it can't
                      reach files above this folder)
  .nojekyll           serve the files as-is; nothing here needs Jekyll
```

## Publishing it

One repository setting, once: **Settings → Pages → Build and deployment → Deploy from a branch →
`main` / `/docs`**. Every push to `main` then republishes it. There is deliberately no workflow — a
folder of static files doesn't need one, and it keeps the Actions minutes for CI and releases.

## Editing it

- **Colours, radii, spacing and shadows are a COPY of `src/Hoard.Desktop/Theme/Tokens.axaml`**, at the
  top of `styles.css`. That file stays the source of truth; when a token changes there, change it here
  too. Nothing else in the stylesheet may name a colour.
- The button, card and badge rules are likewise transcriptions of `Theme/Controls/*.axaml` — including
  the locked rule that neutral variants carry no text shadow. Match the app rather than inventing.
- **The tiles everywhere are procedural gradients, not screenshots.** Real screenshots would carry
  somebody's real boards (the repo's "no real personal data" rule), and gradients cost nothing on the
  wire and stay sharp at any width. The app mock in the hero is the real shell — breadcrumb strip,
  masonry, floating bar — rebuilt in HTML for the same reason.
- The download links point at `releases/latest/download/Hoard-<kind>-<platform>`. Those file names are
  load-bearing (see CLAUDE.md): renaming a release asset breaks these buttons **and** the README's,
  silently.

## Previewing it locally

Either works — the page makes no network requests of its own, so there is nothing for `file://` to
break:

```bash
open docs/index.html                          # macOS: straight into your default browser
python3 -m http.server 8765 --directory docs  # then http://localhost:8765 — Ctrl-C to stop
```

Use the server when you want the exact Pages setup: `docs/` as the site root, so root-relative paths
and the trailing-slash URL behave as they will in production. Otherwise just open the file.

Both are live-edit friendly in the sense that a reload picks up your changes — there is no build step
and nothing to restart, though the server does need a hard reload (⌘⇧R) to get past the browser's
cache on `styles.css`.
