# Marvin — Project website

The public Marvin website. It has three visitor-facing destinations: a
long-scroll overview, a documentation area, and a page for people who want to
get involved. The site is plain HTML, CSS and JavaScript, with no build step and
no framework — the same constraint the controller app is built under, so it
can be served from anywhere.

```
docs/
├── index.html            Overview — the long-scroll introduction
├── build.html            Documentation / Build — parts, print, assemble, flash
├── electronics.html      Documentation / Electronics — soldered path and PCB spec
├── drive.html            Documentation / Drive — command language and controller app
├── reference.html        Documentation / Reference — protocol, pins, files, licensing
├── contribute.html       Get involved — firmware, software, mechanics, electronics
├── assets/
│   ├── css/industry.css  Original component primitives
│   ├── css/site.css      Diagram motion and assembly path switch
│   ├── css/entire.css    Entire brand theme, responsive header and footer
│   ├── fonts/            Self-hosted Entire Headline and Entire Mono
│   ├── js/site.js        Responsive menus, scroll hints, videos, assembly
│   ├── img/              Video posters, adaptive SVG favicon, Apple touch icon
│   └── video/            Demo footage — silent, no audio track
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

## Entire styling

The site uses the logo, headline and monospace fonts, neutral surfaces, indigo
accent, and footer from the sibling `entire.io` checkout. `assets/css/entire.css`
adapts that design to the existing HTML components. Landing sections use full-width
horizontal dividers with a 1280px inner frame, desktop side rails, and compact
section headings matching Entire’s `SiteSection` structure. Headlines use
Entire’s 32px mobile / 40px desktop scale (48px for the homepage). Navigation
collapses into a fullscreen menu below 768px while the header stays 64px tall.
Escape closes the menu; focus and background scrolling are contained while open.
The documentation sidebar becomes its own disclosure below 901px, keeping
page and section links accessible. Wide tables and the board diagram scroll
within their own containers, with keyboard access and overflow hints. Body text uses Entire’s
system sans-serif stack. All font and logo assets are local; there are no
Google Fonts requests, React dependencies, or build requirements.

The Entire logo links to `https://entire.io`; the original Marvin navigation
remains. The color scheme follows the system preference automatically. Footer links use absolute Entire URLs, and the
status link opens the live status page. The header and footer are static HTML
on each of the six pages, so they work with JavaScript disabled. Keep those
shared sections in sync when editing.

To publish, serve `docs/` with any static host (for GitHub Pages, select the
branch and `/docs` folder). Asset links remain relative for project subpaths.

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
  the site's media patterns from LFS for exactly this reason; do not undo it.
  Scope any such rule to the media extensions — a blanket `docs/**` override
  also unsets `diff`, which makes Git treat the HTML, CSS and JS as binary.
- **Take colours, fonts and spacing from the design-system variables**
  (`var(--color-*)`, `var(--font-*)`, `var(--space-*)`) rather than hard-coding
  values. `assets/css/industry.css` is the source of truth for the look.
- **Numbers must carry information.** Keep printed-part identifiers, assembly
  order, pin values and protocol values. Do not number site sections, pages,
  figures or tables; the navigation hierarchy already explains where they are.
- **Motion must degrade.** Every animation has a static fallback and the site
  is complete under `prefers-reduced-motion: reduce`. The exploded view holds
  at its exploded state, which is the more legible of the two anyway.
- **The site is silent.** The clips in `assets/video/` are encoded with no
  audio track at all, the `<video>` tags carry `muted`, and `site.js` re-mutes
  anything that tries to turn the sound on. Keep all three when adding footage:
  strip the audio at encode time with `ffmpeg -i in.mp4 -c:v copy -an out.mp4`.
- Keep `assets/js/site.js` dependency-free.

---

## Behaviour in `site.js`

| Hook | What it does |
| --- | --- |
| `data-action="togglePlay"` / `"toggleDemo"` | Play/pause for the two videos, with the button label kept in step. Under reduced motion the videos hold on their poster frame instead of autoplaying. Both are also held muted at runtime, so nothing can start the sound. |
| `data-ref="anatomyRef"` | The exploded axonometric. Each part carries `data-dx`/`data-dy` — its offset in the exploded state — and scrolling the figure up the viewport interpolates those to zero, so the robot assembles as you read past it. |
| `data-ref="readoutRef"` | Reads *Exploded → Assembling → Assembled* alongside the drawing. |

---

## Figures

Every figure on the site is a real one. The design originally carried **asset
slots** — empty framed boxes marked *To be drawn* or *To be filmed*, each
specifying what the missing artwork should show — but a placeholder reads as an
unfinished page rather than an honest one, so they were removed rather than
left standing. Two went: the per-step assembly drawings on Build, and a video
of a full demo cycle on Drive.

Figures use descriptive names rather than a second, site-wide numbering system.
The assembly drawings are still wanted and are recorded on the Get involved
page's open-problems table, which is where a gap belongs. The demo video is not
planned.

`assets/img/marvin-wiring.webp` shows the wiring as it was
actually built — LiPo, USB-C charger, step-up converter, motor driver, two
gearmotors and two servos, with the six named GPIOs traced from the board to
where they land. Colours are load-bearing here, which is the sharpest case yet
for the no-duotone rule: the accent would flatten the red rail, the black
ground and the four blue signal lines into one hue. Unlike the print-plate
render, the white
surround is kept rather than cut to alpha, because this figure's labels are
black type inside the image and would vanish on a dark page; the box therefore
carries an explicit white plate with an inset, so it reads as a sheet laid on
the page. WebP at q92, 1380 × 750: 186 KB against 1.1 MB as PNG (lossless WebP
was 831 KB — hard type edges on flat white compress badly either way).

The plate scrolls sideways instead of shrinking. Fitted to a phone this figure
lands at about 235 px wide, which is legible as a shape and useless as a
diagram, so it carries a `min-width` of 600 px inside an `overflow-x` box — the
same treatment the wide tables already get. Nothing scrolls above roughly
640 px of column.

Three labels in the source render disagree with the rest of the project and
should be corrected at the source rather than patched in HTML: the driver is
lettered **DRV8811** where the parts table and the firmware say **DRV8833** (a
DRV8811 is a single stepper driver, not a dual H-bridge with IN1–IN4); two pin
labels read `GPI2O` and `GPI1O` for GPIO 20 and GPIO 10; and the board drawn is
a **DevKitM-1** where the table specifies an **ESP32-C3 SuperMini**. The GPIO
assignments themselves match the table exactly.

`assets/img/marvin-controller.webp` shows the controller
connected to a real robot — the cheat sheet it prints on connect, then `D` and
`S` with the replies the firmware sent back. It keeps the app's own dark chrome
in both schemes, untreated: a screenshot of a terminal has to look like the
terminal the reader will meet. WebP at q88, 1588 × 1178: 48 KB against 147 KB
as PNG. Note that the app is a terminal, not a pad-and-slider console — the
slot had been written the other way round.

The two-board block diagram on the Electronics page is drawn rather than photographed:
Electronics page is inline SVG in the page ground's ink, with the accent
reserved for the six conductors that cross the neck joint. It is the one figure
whose subject does not exist, so it had to be a diagram of the specification
rather than a picture of a thing.

`assets/img/marvin-print-plate.webp` shows all eight PLA parts on
one plate. Colours are the source render's, untouched. It is rotated 90° so it sits
landscape rather than portrait, and the light surround around the build plate
is cut to alpha so the plate floats on the page — which is why its box carries
no background fill. Edge pixels are colour-decontaminated, so the cut leaves no
pale fringe when the page is dark. WebP at q95: 113 KB against 1.3 MB as PNG,
visually lossless (47 dB over the visible pixels, alpha bit-identical).

## Figure treatments

Two conventions, and it matters which one a figure gets:

- **Footage is duotoned.** The video figures wrap their `<video>` in
  `.duotone`, which lays the accent over the frame with
  `mix-blend-mode: color`. This is the design system's treatment for
  photography.
- **Technical figures stay neutral.** The exploded axonometric is ink on the
  page ground, and the print plate keeps the colours of
  the render it came from. Duotone forces a single accent hue at one saturation
  and varies only lightness, which flattens exactly the surface shading a parts
  drawing needs to be readable. Do not add `.duotone` to these.

Both kinds still get the `.blueprint` frame with its four corner registration
marks, so they read as one family regardless of treatment.

## The two electronics paths

The site's central distinction is that Marvin's electronics can be **soldered
from four modules today**, or built on **two PCBs that are specified and not
yet designed**. The Electronics page explains that distinction; the Build page
applies it.

The assembly sequence exists once, in `build.html`, and is filtered rather
than written twice: the two paths share every mechanical step and differ in
five. Each `<li>` carries `data-when="both" | "solder" | "pcb"`, and the switch
is a radio group plus `:has()` in `site.css` — **no JavaScript**. That is
deliberate. The thing being hidden is instructions, so a scripted filter would
show a reader whose script had failed a sequence with holes in it and no way to
tell. Step numbers come from a CSS counter, so the visible steps always read
01..n whichever path is chosen; the default state, and the one a browser
without `:has()` is stuck in, is the soldered path — the only one anybody can
build today.

If you add a step, give it a `data-when` and let the counter do the numbering.
Never hard-code a step number, in the markup or in prose that refers to one.

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
