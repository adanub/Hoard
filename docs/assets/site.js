/*
  Hoard — landing page behaviour. Three small jobs, no dependencies:
    1. build the drifting masonry backdrop,
    2. build the tiles inside the app mock,
    3. the theme toggle + the top bar's stuck border.

  The tiles are procedural gradients rather than images on purpose: the repo ships no screenshots of real
  boards (they'd contain somebody's real pins), and gradients stay sharp at any size for nothing on the wire.
*/
(function () {
  "use strict";

  /* Deterministic PRNG (mulberry32) — the same page every visit, so the layout isn't a lottery. */
  function rng(seed) {
    return function () {
      seed |= 0; seed = seed + 0x6D2B79F5 | 0;
      var t = Math.imul(seed ^ seed >>> 15, 1 | seed);
      t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
      return ((t ^ t >>> 14) >>> 0) / 4294967296;
    };
  }

  /* Tile palette: the accent's indigo, its violet/blue neighbours, and a couple of neutrals so the wall
     doesn't read as one colour. Each entry is [hue, saturation%, lightness%] for the gradient's top-left
     sheen; the far end is derived, keeping the app's single top-left light source. */
  var PALETTE = [
    [240, 62, 62], [246, 55, 54], [232, 48, 58], [258, 45, 60],
    [240, 20, 46], [225, 16, 38], [268, 34, 52], [212, 38, 50],
    [246, 70, 68], [235, 12, 30],
    /* A few off-accent hues so a wall of tiles reads as a wall of pictures rather than one swatch.
       Saturation stays moderate — they sit behind the copy, they don't compete with it. */
    [190, 34, 46], [330, 30, 52], [22, 32, 50], [160, 24, 42]
  ];

  function tileBackground(rand) {
    var c = PALETTE[Math.floor(rand() * PALETTE.length)];
    var h = c[0] + Math.round((rand() - 0.5) * 14);
    var s = c[1];
    var l = c[2];
    return "linear-gradient(150deg, hsl(" + h + " " + s + "% " + l + "%) 0%, hsl(" +
           (h + 10) + " " + Math.max(8, s - 16) + "% " + Math.max(10, l - 22) + "%) 100%)";
  }

  /* ── 1. The drifting backdrop ─────────────────────────────────────────── */
  function buildBackdrop() {
    var host = document.getElementById("backdrop-grid");
    if (!host) return;

    var cols = Math.min(9, Math.max(4, Math.round(window.innerWidth / 230)));
    var runHeight = Math.max(2400, Math.ceil(window.innerHeight * 1.8));
    var rand = rng(20240822);
    host.textContent = "";
    builtForHeight = window.innerHeight;

    for (var i = 0; i < cols; i++) {
      var col = document.createElement("div");
      col.className = "backdrop__col";

      var strip = document.createElement("div");
      /* Alternate direction column by column — that opposing drift is the whole effect. */
      strip.className = "backdrop__strip" + (i % 2 ? " is-down" : "");
      strip.style.setProperty("--dur", (95 + Math.round(rand() * 85)) + "s");
      /* Stagger the start so neighbouring columns don't line their seams up. */
      strip.style.animationDelay = "-" + Math.round(rand() * 60) + "s";

      /* One run of tiles, then repeated once. The strip travels exactly -50%, so the repeat lands
         where the original started: a seamless loop (which is also why the tiles carry a bottom
         margin rather than the strip carrying a gap — see the stylesheet).
         The run must outlast the column, which is 160% of the viewport: a fixed height would leave
         blank bands drifting through the lower corners on a tall display. */
      var run = [];
      for (var h = 0; h < runHeight;) {
        var height = 110 + Math.round(rand() * 190);
        run.push({ h: height, bg: tileBackground(rand) });
        h += height + 18;
      }

      for (var pass = 0; pass < 2; pass++) {
        for (var t = 0; t < run.length; t++) {
          var tile = document.createElement("div");
          tile.className = "backdrop__tile";
          tile.style.height = run[t].h + "px";
          tile.style.background = run[t].bg;
          strip.appendChild(tile);
        }
      }

      col.appendChild(strip);
      host.appendChild(col);
    }
  }

  /* Rebuild only when the column count would actually change — a rebuild restarts every animation, and
     mobile browsers fire resize on every address-bar nudge. */
  var lastCols = 0;
  var builtForHeight = 0;
  function maybeRebuild() {
    var cols = Math.min(9, Math.max(4, Math.round(window.innerWidth / 230)));
    /* Height matters as well as width: a window grown taller than the run needs a longer run. */
    if (cols === lastCols && window.innerHeight <= builtForHeight) return;
    lastCols = cols;
    buildBackdrop();
  }

  /* ── 2. The app mock's grid ───────────────────────────────────────────── */
  function buildMock() {
    var host = document.getElementById("mock-masonry");
    if (!host) return;

    var rand = rng(97531);
    /* Five columns, each overfilled past the clip so the grid reads as scrolled-into rather than ended.
       Heights are fixed per column, not random, so the staggered tops look composed. */
    var columns = [
      [150, 96, 210, 128, 170],
      [104, 188, 132, 160, 120],
      [176, 120, 96, 208, 140],
      [128, 164, 184, 108, 150],
      [96, 200, 124, 172, 132]
    ];
    var tags = { "0,2": "GIF", "3,1": "VIDEO", "2,0": "GIF" };

    columns.forEach(function (heights, c) {
      var col = document.createElement("div");
      col.className = "mock__col";

      heights.forEach(function (h, r) {
        var tile = document.createElement("div");
        tile.className = "mock__tile";
        tile.style.height = h + "px";
        tile.style.background = tileBackground(rand);

        var label = tags[c + "," + r];
        if (label) {
          var tag = document.createElement("span");
          tag.className = "tag";
          tag.textContent = label;
          tile.appendChild(tag);
        }
        col.appendChild(tile);
      });

      host.appendChild(col);
    });
  }

  /* ── 3. Chrome ────────────────────────────────────────────────────────── */
  function wireTheme() {
    var button = document.getElementById("theme-toggle");
    var icon = document.getElementById("theme-icon");
    if (!button || !icon) return;

    function paint() {
      var dark = document.documentElement.dataset.theme !== "light";
      /* The icon shows the theme the click will GIVE you, matching the label beside it. */
      icon.firstElementChild.setAttribute("href", dark ? "#i-sun" : "#i-moon");
      button.setAttribute("aria-label", dark ? "Switch to the light theme" : "Switch to the dark theme");
      var meta = document.querySelector('meta[name="theme-color"]');
      if (meta) meta.setAttribute("content", dark ? "#212025" : "#E4E8EF");
    }

    button.addEventListener("click", function () {
      var next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem("hoard-theme", next); } catch (e) { /* private mode */ }
      paint();
    });

    paint();
  }

  function wireTopbar() {
    var bar = document.getElementById("topbar");
    if (!bar) return;
    var onScroll = function () { bar.classList.toggle("is-stuck", window.scrollY > 8); };
    window.addEventListener("scroll", onScroll, { passive: true });
    onScroll();
  }

  maybeRebuild();
  buildMock();
  wireTheme();
  wireTopbar();

  var resizeTimer;
  window.addEventListener("resize", function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(maybeRebuild, 250);
  });
})();
