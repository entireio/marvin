# Marvin — Project website

The public Marvin website. Five pages of plain HTML, CSS and JavaScript, with
no build step and no framework — the same constraint the controller app is
built under, so it can be served from anywhere.

```
docs/
├── index.html            01 Marvin     — long-scroll home
├── build.html            02 Build      — print, electronics, assemble, flash
├── drive.html            03 Drive      — the command language, controller app
├── contribute.html       04 Contribute — firmware, software, mechanics, electronics
├── reference.html        05 Reference  — protocol, pin map, parts, licensing
├── assets/
│   ├── css/industry.css  Design-system tokens and components (verbatim from the design)
│   ├── css/site.css      Page styles lifted from the design artboards
│   ├── js/site.js        Theme toggle, video controls, exploded-view assembly
│   ├── img/              Video posters and the favicon
│   └── video/            Demo footage
├── DESIGN-BRIEF.md       The brief the design was made from
├── convert-artboards.py  How the design artboards became these pages
└── .nojekyll             Stops GitHub Pages hiding files that start with "_"
```

---

## Running it locally

Any static file server. For example:

```bash
cd docs && python3 -m http.server 4173
```

Then open <http://127.0.0.1:4173>. Opening `index.html` as a `file://` URL
mostly works, but relative asset paths and the fonts behave better over HTTP.

---

## Where this came from

The design was produced in [Claude Design](https://claude.ai/design) from
[`DESIGN-BRIEF.md`](DESIGN-BRIEF.md), and exported as canvas artboards
(`.dc.html`). Those artboards do not run outside the design canvas: they are
wrapped in `<x-dc>`, carry a `<helmet>` in place of a `<head>`, and use
React-style bindings (`{{ handler }}`, `ref=`, `onClick=`, camelCase boolean
attributes) resolved by a runtime that is not shipped with the export.

[`convert-artboards.py`](convert-artboards.py) performs that conversion —
stripping the canvas wrapper, rebuilding a real `<head>`, inlining the
`MarvinSlot` component, rewriting links and asset paths, and replacing the
bindings with `data-action` / `data-ref` hooks. The behaviour those bindings
used to provide is reimplemented in [`assets/js/site.js`](assets/js/site.js).

**The generated pages are the source now.** Edit the HTML directly; the script
is kept for reference, not as a build step. Re-running it would discard any
hand-edits.

---

## Conventions

- **Every path must stay relative.** The site is published from a project
  subpath (`/marvin/`), not a domain root, so a path beginning with `/` breaks
  in production while working perfectly on localhost. This is the single most
  common way a GitHub Pages site ships broken.
- **Nothing the site loads may go through Git LFS.** GitHub Pages does not
  resolve LFS pointers — an LFS-tracked image arrives as a ~130-byte text file
  and the page breaks silently. [`.gitattributes`](../.gitattributes) exempts
  `docs/**` from LFS for exactly this reason; do not undo it.
- **Take colours, fonts and spacing from the design-system variables**
  (`var(--color-*)`, `var(--font-*)`, `var(--space-*)`) rather than hard-coding
  values. `assets/css/industry.css` is the source of truth for the look.
- **Motion must degrade.** Every animation has a static fallback and the site
  is complete under `prefers-reduced-motion: reduce`. The exploded view holds
  at its exploded state, which is the more legible of the two anyway.
- Keep `assets/js/site.js` dependency-free.

---

## Behaviour in `site.js`

| Hook | What it does |
| --- | --- |
| `data-action="toggleTheme"` | Switches colour scheme and stores the choice. With nothing stored, `prefers-color-scheme` stays in charge. An inline script in each `<head>` applies a stored choice before first paint, so the other scheme never flashes. |
| `data-action="togglePlay"` / `"toggleDemo"` | Play/pause for the two videos, with the button label kept in step. Under reduced motion the videos hold on their poster frame instead of autoplaying. |
| `data-ref="anatomyRef"` | The exploded axonometric. Each part carries `data-dx`/`data-dy` — its offset in the exploded state — and scrolling the figure up the viewport interpolates those to zero, so the robot assembles as you read past it. |
| `data-ref="readoutRef"` | Reads *Exploded → Assembling → Assembled* alongside the drawing. |

---

## Still to come

The design carries **asset slots** — marked *To be drawn*, *To be filmed* or
*To be captured* — where artwork is still needed. Each slot states what the
drawing must show, at what aspect ratio, in what style. Together they are the
shot list:

| Fig. | Needed | Page |
| --- | --- | --- |
| 06 | Print plate layout, all nine parts | Build |
| 07 | Wiring diagram, as built | Build |
| 08 | Assembly step drawing | Build |
| 09 | Screenshot of the controller app, connected | Drive |
| 10 | Video of one full demo cycle | Drive |

The assembly instructions themselves do not exist yet, and the Build page says
so rather than pretending otherwise.

---

## Deployment

There are two ways to publish this folder, for the two stages the project is
passing through.

### While the site is still private — Cloud Run

A GitHub Pages site is **publicly reachable by anyone with the URL**, even when
the repository itself is private, and restricting who can view one requires
GitHub Enterprise Cloud. So until the project is ready to be public, the site is
served instead by [`server/`](../server/README.md) — the same files, behind a
GitHub sign-in, on Google Cloud Run:

```bash
./deploy_google_cloud.sh --help
```

Only the GitHub accounts you name can read it. See
[`server/README.md`](../server/README.md) for the settings and how the sign-in
works.

### Once the site is public — GitHub Pages

**Settings → Pages → Source: Deploy from a branch → `main` / `/docs`**. Nothing
in this folder needs to change to move between the two: the relative-path rule
below is what lets the same files work at a project subpath (`/marvin/`) on
Pages and at the domain root on Cloud Run.

---

## Licence

The website source is covered by the project's [MIT Licence](../LICENSE).
