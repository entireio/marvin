# Marvin — Website Design Brief

**For:** Claude Design
**Deliverable:** design for the public Marvin project website
**Hosting:** GitHub Pages, static, served from a repository subfolder

This document is self-contained. You do not need access to the source
repository — every fact, table, and piece of content you need is reproduced
here. Where something does not exist yet, it is marked explicitly rather than
invented; please design around those gaps rather than filling them with
plausible-sounding fiction.

---

# 1. What Marvin is

Marvin is a small **tank-tracked robot with an expressive two-axis head**,
released as open source hardware. It is roughly the size of a large book. It
drives on two rubber tracks, and its head pans and tilts on two servos, which is
the whole of its body language.

The project's purpose is **reproducibility**: anyone with a 3D printer and a
soldering iron should be able to build one. Every part of it — firmware,
electronics, mechanical design, and the app you drive it with — is published
under an open licence.

It is currently an **early prototype**. The drivetrain, the head, and the
Bluetooth control link work today. The sensing, the custom circuit board, and
the eye displays do not exist yet.

## 1.1 What makes it worth a website

Three things, in order of importance:

1. **It is a designed physical object.** It has nine printed parts, numbered
   01 through 09. It has a chassis, tracks, a neck, and a head. The website's
   job is to make someone want to build one.
2. **It has a personality.** This is not marketing language — the personality is
   literally encoded in the firmware. See §2.
3. **It is genuinely open.** Not "source available" — properly licensed, with
   four distinct ways to contribute (firmware, software, mechanics,
   electronics).

## 1.2 What the website must do

Guide a visitor through, in this order:

- Understand what Marvin is and want one
- **Print** the parts
- **Assemble** it
- **Use** it
- **Contribute** — to the firmware, to the software, or to the mechanical and
  electronic design

---

# 2. Character and voice

Marvin's autonomous demo routine is a hand-written table of movements in the
firmware. The comments in that file are the truest statement of the project's
character that exists, and they should inform the site's tone. Verbatim:

> The patterns are meant to feel organic:
> - The head often moves *before* the body turns (anticipation).
> - Pauses with head sweeps simulate curiosity / scanning.
> - Asymmetric durations avoid a robotic, metronomic feel.
> - A mix of arcs, pivots, and straight runs keeps it lively.

Individual movement steps are annotated:

```
{  0, 0, CENTER + 40, CENTER - 15,  800 },   // glance right-up
{  0, 0, CENTER + 50, CENTER - 20,  700 },   // tilt up (surprised?)
{  0, 0, CENTER + 35, CENTER,       500 },
{  0, 0, CENTER - 10, CENTER,       300 },   // snap back
{  0, 0, CENTER + 35, CENTER - 15,  600 },   // look again!
```

That sequence is a **double-take**. Someone sat down and choreographed a robot
doing a double-take, in a C++ struct array.

**Voice: dry, understated wit.** Precise and restrained, with occasional
dryness. The name nods to the perpetually depressed robot from *The
Hitchhiker's Guide to the Galaxy*, and the site may acknowledge this — but
lightly, once, as a knowing aside. Do not build the site around the joke, and
do not write Marvin as depressed. This Marvin is *curious*, which is more
interesting and is what the firmware actually does.

Write like a well-edited technical publication that trusts its reader. No
exclamation marks, no "awesome", no growth-marketing verbs. Short declarative
sentences. The humour should come from precision and understatement, never from
being zany.

---

# 3. Audience

Four people arrive at this site. Design for all four; they need different depths.

| Visitor | Arrives from | Wants | Gives you |
| --- | --- | --- | --- |
| **The curious** | A link, a post, a search | To understand what this is in 30 seconds and see it move | Attention, if you earn it fast |
| **The builder** | Wants to make one | Parts list, print settings, wiring, assembly steps, flashing | Patience, but needs precision |
| **The contributor** | Knows the project | Where the code is, what the conventions are, what needs doing | Skill — do not waste it |
| **The lurker** | Evaluating seriously | Licence terms, project maturity, whether it is maintained | Judgement |

The most common failure mode for a project like this is a beautiful landing page
that abandons the builder at step one. **The build path is the product.** Design
it with the same care as the hero.

---

# 4. Design direction

## 4.1 The reference point

**An architecture or industrial-design monograph.** Think *El Croquis*, *2G*,
*A+U*, or *Detail* — technical publications that are also beautiful objects.
Also relevant: the Vitsœ website, Braun's product documentation under Dieter
Rams, and Ulm School technical illustration.

The governing qualities are **restraint, precision, and generous space**. The
page should feel like good paper. Nothing decorative. Every rule, number, and
margin should look deliberate.

Explicitly *not*: startup landing page, gradient meshes, glassmorphism, floating
3D blobs, neon, "AI aesthetic", oversized rounded cards, drop shadows,
emoji-as-iconography.

## 4.2 The central idea: numbered parts as the design system

Marvin's printed parts are already numbered **01–09**. This numbering is the
site's organising motif, for free. Use it:

- Number the site's own sections `01`–`05`, as a booklet numbers its chapters
- Use the same numeric callouts on drawings that the parts list uses
- Let figure references (`Fig. 03`) behave like a real technical publication

Numerals should be a *deliberate typographic feature* — large, set in a
distinctive face, used as anchors down the page. This is the single strongest
design idea available; please lean on it.

## 4.3 Technical drawing as the visual language

The robot will be shown primarily through **line drawings exported from CAD** —
orthographic elevations, exploded axonometrics, and part drawings — not
photography. This suits both the aesthetic and the project's honesty about being
a set of files rather than a product.

Two specific opportunities:

- **The exploded axonometric is the hero.** All nine parts, separated along
  assembly axes, with numbered callouts. As the visitor scrolls, it assembles.
  This is simultaneously the most beautiful thing on the site and the clearest
  possible explanation of how it goes together.
- **Assembly steps as wordless line drawings.** The IKEA-instruction convention
  — numbered, drawn, minimal text — is both period-appropriate for the booklet
  aesthetic and genuinely the clearest way to show assembly.

## 4.4 Typography

Set the site with **three faces at most**, from Google Fonts (a hosting
constraint — see §8):

| Role | Purpose | Suggestions |
| --- | --- | --- |
| **Display** | Section numerals, headings, the wordmark | *Instrument Serif*, *Newsreader*, *Fraunces*, or a single grotesque at a heavy weight |
| **Text** | Body copy, long-form instructions | *Inter*, *Schibsted Grotesk*, *Archivo*, *Source Serif 4* |
| **Data** | Pin maps, commands, part numbers, code | *JetBrains Mono* or *IBM Plex Mono* |

Two routes, both valid — **your call, pick one and commit**:

- **Editorial contrast** — a high-contrast display serif against a neutral
  grotesque. Warmer, more monograph-like, better for the numerals.
- **Single-family rigour** — one neutral grotesque throughout, differentiated
  only by weight, size, and space. More Swiss, more severe, harder to get wrong.

Non-negotiables: generous leading in body copy, a measure of **60–75
characters**, and a small number of type sizes used consistently. Restraint here
is most of the effect.

## 4.5 Colour

An ink-on-paper palette:

- **Paper** — a warm off-white, not pure `#FFFFFF`
- **Ink** — a near-black with a hint of warmth, not pure `#000000`
- **Greys** — two or three, for rules, captions, and secondary text
- **One accent, used sparingly.** A vermilion or technical-drawing blue,
  reserved for part callouts, figure numbers, and links. If the accent appears
  more than a few times per screen, it is being overused.

**Dark mode is required** (see §8). Do not simply invert — design a considered
dark variant with a warm near-black ground and warm off-white ink, keeping the
same restraint.

> **Note on the existing app.** The Bluetooth controller app currently uses a
> dark neon-terminal palette (`#0d0f1a` ground, `#00e5a0` green). That is the
> opposite of this direction. It is a separate tool, not part of this site, so
> the divergence is acceptable for now — but if you want to propose a palette
> that could eventually unify the two, that would be genuinely useful.

## 4.6 Motion

The site is scroll-animated, but the motion must be **restrained and
meaningful**. Every animation should be doing explanatory work.

Good, in priority order:

1. The exploded view assembling as you scroll — the centrepiece
2. Line drawings that draw themselves (SVG `stroke-dashoffset` — cheap and
   exactly right for this aesthetic)
3. Callout numbers appearing in sequence around a drawing
4. The head pan/tilt range demonstrated as an animated diagram
5. Restrained typographic entrances — brief, small offsets, no bounce

Not acceptable: parallax for its own sake, everything fading up on scroll,
scroll-jacking, long-running loops competing for attention, motion that delays
reading.

**Every animation must have a static fallback**, and the site must be complete
and legible under `prefers-reduced-motion: reduce`.

---

# 5. Asset inventory

## 5.1 What will exist

- **Line drawings and renders exported from the CAD model** (Rhino) —
  orthographic views, exploded axonometric, individual part drawings
- **Video of the robot running its demo sequence** — driving, pivoting, the head
  scanning and double-taking

## 5.2 What does NOT exist yet

**There is currently no image of any kind in the repository.** No photographs,
no renders, no drawings, no logo, no wordmark.

Therefore:

- **Design with clearly-marked asset slots**, each specifying exactly what
  drawing or clip belongs there, at what aspect ratio, and what it must show.
  The slot specification is part of the deliverable — it becomes the shot list.
- **Use placeholders that are CSS/SVG-drawn**, not stock imagery, so the design
  reads correctly before assets arrive and degrades honestly if one is missing.
- **A wordmark is in scope.** "MARVIN" set with real care, in the display face,
  is probably sufficient — resist designing a mascot or icon.

Video guidance: hero video should be muted, looping, and short, with a
poster-frame fallback and a static first paint. It must never block the page.

---

# 6. Information architecture

Five sections, numbered like a booklet's chapters. A long-scroll home for the
narrative; proper pages where real depth is needed.

```
01  Marvin      Home — long-scroll narrative
02  Build       Print → Electronics → Assemble → Flash
03  Drive       Using it, the command language, the controller app
04  Contribute  Firmware · Software · Mechanics · Electronics
05  Reference   Protocol, pin map, parts list, licensing
```

## 01 — Home (long scroll)

| # | Section | Content | Motion |
| --- | --- | --- | --- |
| 1 | **Hero** | Wordmark, one-sentence definition, the robot moving | Video, or the exploded view holding still before assembling |
| 2 | **What it is** | Two or three short paragraphs. The subsystem status table (§7.1) | Minimal |
| 3 | **Anatomy** | The exploded axonometric with numbered callouts — the centrepiece | Assembles on scroll; callouts appear in sequence |
| 4 | **It is curious** | The personality section. Quote the firmware comments (§2). Show the pan/tilt envelope | Animated head-range diagram; demo video |
| 5 | **Build one** | Three-step overview — print, wire, flash — each linking into 02 | Restrained |
| 6 | **Open** | The licence split (§7.8), the four contribution routes, link to the repository | Minimal |

The hero must state plainly what this is. A visitor who sees only the first
screen should be able to say "it's an open source 3D-printed tracked robot."

## 02 — Build

The most important page on the site, and the one most likely to be
under-designed. Sections, in build order:

1. **Print** — parts table (§7.5), materials, print settings (§7.6),
   orientation and support notes
2. **Electronics** — wiring table (§7.4), what to buy, the brownout warning
3. **Assemble** — step-by-step. **This content does not exist yet** (§9)
4. **Flash** — installing the firmware (§7.2)
5. **First drive** — connecting over Bluetooth, first commands

Design needs: a parts table that is genuinely pleasant to work from, print
settings that read as a spec sheet, and a step format that works for wordless
drawings. Assume someone is reading this on a phone, next to a printer, with
resin on their hands.

## 03 — Drive

- Connecting to the browser-based controller (§7.3), including the browser
  support matrix and the `localhost` requirement
- **The command language** (§7.7) — a designed reference table, not a code dump.
  This is a nice small design problem: seven commands, each a letter and a
  number
- The demo mode, and what the robot does when left alone
- Troubleshooting (§7.9)

## 04 — Contribute

Four clearly separated routes, because they attract different people and need
different framing:

| Route | For | Needs |
| --- | --- | --- |
| **Firmware** | Embedded C++ | Toolchain, architecture, conventions, how to add a command |
| **Software** | Web | The controller app and this website |
| **Mechanics** | CAD, printing | Master model, export conventions, part numbering |
| **Electronics** | PCB design | The board that does not exist yet — the biggest open invitation |

Include the conventions that actually matter (§7.10) and the current open
problems (§9). Being specific about what needs doing is what converts a reader
into a contributor.

## 05 — Reference

The dense material, designed as reference rather than narrative: full command
protocol, Bluetooth service details, pin map, parts list, licensing. This page
is allowed to be a table-heavy document — make the tables beautiful.

---

# 7. Content

Real content, reproduced for use in the design. Everything below is accurate as
of this brief.

## 7.1 Subsystem status

| Subsystem | Status | Detail |
| --- | --- | --- |
| Drivetrain — two DC motors, tank tracks | Working | DRV8833 dual H-bridge, PWM speed control |
| Head — 2-axis pan and tilt | Working | Two SG90-class servos, smoothed motion |
| Connectivity — Bluetooth Low Energy | Working | Nordic UART Service, browser-controllable |
| Demo mode — autonomous exploration | Working | Hand-choreographed movement sequence |
| Odometry — wheel encoders | Planned | For closed-loop speed and distance |
| Distance sensing — time-of-flight | Planned | Obstacle avoidance |
| Social sensing — infrared | Planned | Marvin-to-Marvin detection |
| Expression — two displays as eyes | Planned | — |
| Custom PCB | Planned | Replaces the current breakout-board wiring |

Four of nine subsystems work. **Present this honestly** — a status table that
admits what is unfinished reads as more credible than one that does not, and it
doubles as a to-do list for contributors.

## 7.2 Getting the firmware on the robot

Requires PlatformIO.

```
cd firmware && ./flash.sh
```

Then a serial monitor at **115200 baud** gives a `Marvin>` prompt.

The board is an **ESP32-C3 SuperMini**. Bluetooth advertising starts
automatically under the name `Marvin`.

## 7.3 Driving it from a browser

Requires Chrome or Edge — Web Bluetooth does not exist in Firefox or Safari.

```
cd controller && ./serve.sh
```

Open `http://localhost:3000`, click **Scan**, choose `Marvin`.

| Browser | Web Bluetooth |
| --- | --- |
| Chrome (desktop, Android) | Yes |
| Edge | Yes |
| Opera | Yes |
| Firefox | No |
| Safari (macOS, iOS) | No |

**Why it must be served, not opened as a file:** Web Bluetooth only works in a
secure context — `https://` or `localhost`. Opening the file directly with
`file://` leaves the Bluetooth API undefined. This trips up nearly everyone; it
deserves a visible callout in the design, not a footnote.

## 7.4 Wiring

| Component | Part | Connection |
| --- | --- | --- |
| Microcontroller | ESP32-C3 SuperMini | — |
| Motor driver | DRV8833 breakout | IN1→GPIO 5, IN2→GPIO 6, IN3→GPIO 20, IN4→GPIO 10 |
| Drive motors | 2 × brushed DC gearmotor | DRV8833 outputs A and B (A and B are not yet formally assigned to left and right) |
| Head pan servo | SG90-class | GPIO 4 |
| Head tilt servo | SG90-class | GPIO 1 |

Pin map, as a standalone reference:

| Function | GPIO |
| --- | --- |
| Servo — tilt | 1 |
| Servo — pan | 4 |
| Motor A — IN1 / IN2 | 5 / 6 |
| Motor B — IN3 / IN4 | 20 / 10 (wired inverted) |

Two details worth surfacing in the design, because both are the kind of thing
that costs a builder an evening:

- Motor B's inputs are **wired inverted** so that a positive speed on both
  channels drives forward without the firmware correcting per side.
- The firmware drives all four motor pins **LOW at the very start of startup**,
  before anything else, because ESP32 pins float during boot and a floating
  H-bridge input makes the tracks lurch.

## 7.5 Printed parts

Numbered in assembly order: chassis, then drivetrain, then head.

| # | Part | Qty | Material |
| --- | --- | --- | --- |
| 01 | Body / chassis | 1 | PLA |
| 02 | Track cover, left | 1 | PLA |
| 03 | Track cover, right | 1 | PLA |
| 04 | Wheel | 4 | PLA |
| 05 | Track link | many | TPU |
| 06 | Head base | 1 | PLA |
| 07 | Head cover | 1 | PLA |
| 08 | Neck | 1 | PLA |
| 09 | Neck mount | 1 | PLA |

These nine numbers are the site's spine. Wheel and track-link quantities are not
yet confirmed.

## 7.6 Print settings

**Not yet validated — these are starting points, and the site should say so.**

| | |
| --- | --- |
| Material | PLA for structure, TPU for track links |
| Layer height | 0.2 mm |
| Walls | 3 |
| Infill | 20% structural, 40% neck and head mount |
| Supports | Required on the body and head cover |

The track links are the one part that genuinely needs flexible filament —
printed rigid, you get a robot that cannot corner. Good, quotable detail.

## 7.7 The command language

One line-based text protocol, over both USB serial and Bluetooth. Commands are
case-insensitive, one per line.

| Command | Range | Meaning |
| --- | --- | --- |
| `R<angle>` | 0–180 | Head rotation (pan) — e.g. `R90` |
| `T<angle>` | 30–85 | Head tilt — e.g. `T45` |
| `M<speed>` | −255–255 | Both motors — e.g. `M128` |
| `A<speed>` | −255–255 | Motor A only (one track) |
| `B<speed>` | −255–255 | Motor B only (the other track) |
| `S` | — | Stop |
| `D` | — | Toggle demo mode |

Behavioural notes worth designing around: any movement command **cancels demo
mode automatically**, and values are **clamped rather than rejected** — ask for
`T200` and you get 85°, the mechanical limit, not an error.

Tilt is deliberately limited to **30°–85°** (centre 57°) to protect the neck
from its mechanical stops. Pan is the full **0°–180°**, centred at 90°. This
asymmetric envelope is a nice thing to draw.

Bluetooth uses the **Nordic UART Service**, the de-facto standard for
serial-over-Bluetooth, which means any generic BLE terminal can drive the robot
— not only the project's own app:

| UUID | Role |
| --- | --- |
| `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` | Service |
| `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` | RX — client writes commands |
| `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` | TX — robot returns responses |

## 7.8 Licensing

The conventional open-hardware split, and worth presenting clearly since it is
part of the project's seriousness:

| Covers | Licence |
| --- | --- |
| **Software** — firmware, controller app, website | MIT |
| **Hardware** — mechanical design, electronics | CERN-OHL-S-2.0 |

CERN-OHL-S is strongly reciprocal: build and distribute a modified Marvin, and
you share your design changes too.

## 7.9 Troubleshooting

| Symptom | Cause |
| --- | --- |
| Scan button does nothing | Not a secure context — use `localhost`, not `file://` |
| "Web Bluetooth is not supported" | Firefox or Safari — use Chrome or Edge |
| `Marvin` not in the device picker | Not powered, or already connected elsewhere — BLE allows one client at a time |
| Connects, then drops immediately | Low battery — the microcontroller browns out when the motors draw current |
| Robot lurches on power-up | Motor pins floating during boot |

## 7.10 Contribution conventions

The rules that actually matter, for the Contribute section:

- **All pin numbers and tuning constants live in one configuration header.**
  Never hard-code a pin inside a driver. This is what makes moving to a
  different microcontroller a config change rather than a rewrite.
- **The main loop never blocks.** Every periodic behaviour is a timer-based
  state machine. No `delay()` — it would stall Bluetooth and the console.
- **One class per peripheral**, in its own header/source pair.
- **Adding a command touches four places**: the parser, the on-device help text,
  the controller app's cheat sheet, and the protocol documentation.
- **The controller app has no build step and no framework**, deliberately, so it
  can be served from anywhere. Keep it that way.
- **CAD is the master; meshes are derived.** Never edit a mesh directly.
- **Nothing over ~50 MB in the repository.** Oversized meshes are almost always
  over-tessellated rather than genuinely complex.

## 7.11 Repository structure

For the Contribute and Reference sections — where things actually live:

| Folder | Contains | State |
| --- | --- | --- |
| `firmware/` | ESP32 firmware, C++ / PlatformIO / Arduino | Working |
| `controller/` | The browser-based Bluetooth control app | Working |
| `electronics/` | Schematics, PCB, BOM, power design | Scaffolding only |
| `mechanical/` | CAD master model and printable meshes | Meshes exist |
| `docs/` | This website | Being designed |

Firmware source, for the architecture description:

| File | Responsibility |
| --- | --- |
| `config.h` | Every pin and tuning constant — the single source of truth |
| `main.cpp` | Startup, command parser, main loop |
| `motor.*` | One DC motor channel on the H-bridge |
| `head_servos.*` | Pan/tilt pair with smooth interpolation |
| `ble_serial.*` | Bluetooth serial transport |
| `demo.*` | The autonomous movement sequence |

A detail worth using: the head servos hold a *target* angle and walk the
*current* angle toward it in small steps on a timer. That interpolation is the
entire reason the head movement reads as organic rather than mechanical. It is
about ten lines of code, and it is the difference between a machine and a
character.

---

# 8. Technical constraints

**These are hard requirements, not preferences.**

1. **Static site on GitHub Pages.** No server, no database, no server-side
   rendering. HTML, CSS, JavaScript, and static assets only.
2. **Served from a subpath.** The site will live at
   `https://spedemon.github.io/marvin/`, not at a domain root. **All asset and
   link paths must be relative** — absolute paths beginning with `/` will break.
   This is the single most common way a GitHub Pages site ships broken.
3. **Fonts from Google Fonts** or self-hosted files. No other third-party font
   services.
4. **No build step preferred.** Plain HTML, CSS, and JavaScript is ideal and
   matches how the controller app is built. If a build step is genuinely
   necessary, say so explicitly.
5. **Dark mode required**, via `prefers-color-scheme`, designed rather than
   inverted.
6. **`prefers-reduced-motion: reduce` must be honoured.** The site must be
   complete and readable with all motion disabled.
7. **Accessible.** Real semantic HTML, keyboard-navigable, WCAG AA contrast, alt
   text on every drawing. A technical publication that cannot be read by a
   screen reader is a failed technical publication.
8. **Fast.** Line drawings should be inline SVG, not raster. Video must not
   block first paint. Assume a phone on hotel wifi.
9. **Responsive**, and genuinely usable on a phone — see §6, the builder is
   standing at a printer.

---

# 9. What does not exist yet

Please design around these honestly. Roughly half the build guide will be
incomplete at launch, and **a design that handles this gracefully is worth more
than one that assumes finished content.** A printed technical booklet has a
convention for this — a section marked "to be issued" — and something in that
spirit would suit the aesthetic exactly.

Missing entirely:

- **Assembly instructions.** No step-by-step exists in any form. This is the
  largest gap, and it sits in the middle of the most important page.
- **A fasteners and hardware BOM** — screws, bearings, motor specifications.
- **Anything electronic beyond breadboard wiring** — no schematic, no PCB, no
  parts list, no battery selection, no charging circuit.
- **Validated print settings.** The figures in §7.6 are estimates.
- **Physical dimensions and weight** of the assembled robot.
- **Confirmed quantities** for wheels and track links.
- **Any imagery at all** (§5).

Design implication: **every one of these deserves a considered empty state.**
Ideally an empty state that recruits — "this section is not written yet; here is
what it needs and how to help" is far better than a grey box, and turns the
project's incompleteness into its clearest call for contributors.

---

# 10. Deliverable

Please produce:

1. **The home page** in full — this carries the aesthetic and the anatomy
   centrepiece.
2. **The Build page** in full — the hardest and most important layout, including
   the parts table, the print spec, and the assembly step format with its empty
   state.
3. **A representative deep page** — Drive or Contribute — establishing how
   secondary pages behave.
4. **The design system**: type scale, colour tokens (light and dark), spacing
   and grid, rules and dividers, table styling, figure and callout conventions,
   button and link treatments.
5. **An asset shot list** — for each image slot, what drawing or clip is needed,
   at what aspect ratio, showing what. This becomes the CAD export and filming
   list, so please be specific.
6. **Motion specification** — what animates, triggered by what, over what
   duration, and what each falls back to under reduced motion.

## Priorities, if something has to give

1. Typography and spatial restraint — the whole aesthetic rests here
2. The build path actually working as instructions
3. The exploded-view anatomy centrepiece
4. Everything else

## The test

A visitor should come away with two impressions, in this order:

**"This is a serious, well-made thing."**
**"I could build one."**

If the design achieves the first but not the second, it has failed — this is
documentation before it is a landing page. If it achieves the second without the
first, it is merely a wiki.
