# Marvin — Mechanical

CAD sources and printable meshes for Marvin's chassis, tracks, and head.

```
mechanical/
├── src/        CAD master model (Rhino 3D, .3dm)
├── stl/        Meshes exported for printing
└── README.md   You are here
```

---

## Parts

Parts are numbered in assembly order: chassis, then drivetrain, then head.

| # | Part | File | Qty | Material |
| --- | --- | --- | --- | --- |
| 01 | Body / chassis | `01_body.stl` † | 1 | PLA |
| 02 | Track cover, left | `02_track_cover_left.stl` | 1 | PLA |
| 03 | Track cover, right | `03_track_cover_right.stl` | 1 | PLA |
| 04 | Wheel | `04_wheel.stl` | 4 | PLA |
| 05 | Track link | `05_track.stl` | many | TPU |
| 06 | Head base | `06_head_base.stl` | 1 | PLA |
| 07 | Head cover | `07_head_cover.stl` | 1 | PLA |
| 08 | Neck | `08_neck.stl` | 1 | PLA |
| 09 | Neck mount | `09_neck_mount.stl` | 1 | PLA |

† Distributed separately — see [Large files](#large-files) below.

The numeric prefix is the only thing tying a mesh to its place in the assembly,
so keep the sequence contiguous and unique. If you add a part, renumber rather
than reusing or skipping a number, and update this table in the same commit.

Wheel and track-link quantities are not yet confirmed against the assembly, and
there is no printed-parts BOM with fasteners, bearings, and motor
specifications yet. Both are open items.

---

## Print settings

Not yet characterised — these are starting points, not validated settings.

| | |
| --- | --- |
| Material | PLA for structure; TPU for the track links |
| Layer height | 0.2 mm |
| Walls | 3 |
| Infill | 20 % (structural parts), 40 % (neck and head mount) |
| Supports | Needed on the body and head cover |

The track links are the one part that genuinely needs a flexible filament —
printing them rigid gives you a robot that cannot corner. If you print a set,
please open an issue with your material, printer, and what worked.

---

## Large files

Two files exceed what GitHub will accept and are **not in this repository**:

| File | Size | Why it is excluded |
| --- | --- | --- |
| `src/marvin_design.3dm` | 2.7 GB | Over GitHub's 100 MB file limit, and over Git LFS's 2 GB per-file cap |
| `stl/01_body.stl` | 454 MB | Over GitHub's 100 MB hard limit |

Both are listed in [`.gitignore`](../.gitignore). To get them, ask for a copy —
or, better, help fix the underlying problem.

**The real issue is tessellation, not complexity.** A 454 MB binary STL is
roughly nine million triangles for a part that is essentially a printed box. Any
slicer handles a few hundred thousand triangles for a part this size without
losing visible detail. Re-exporting `01_body.stl` from Rhino at a coarser mesh
tolerance should bring it under 50 MB, at which point it belongs in the
repository like every other part.

Everything else in `stl/` **is** tracked, through Git LFS. Install LFS before
cloning or you will get pointer files instead of geometry:

```bash
git lfs install
git clone https://github.com/spedemon/marvin.git
```

Already cloned? `git lfs install && git lfs pull`.

---

## Conventions

- **`src/` is the master; `stl/` is derived.** Never edit a mesh directly — make
  the change in CAD and re-export, or the two drift apart irrecoverably.
- Export in **binary** STL, never ASCII: same geometry, roughly a fifth the size.
- Check the size of a mesh before committing it. Anything approaching 50 MB is
  over-tessellated; re-export rather than commit.
- Keep the numeric prefixes and update the table above when parts are added,
  renamed, or renumbered.
- Servo travel limits in
  [`firmware/src/config.h`](../firmware/src/config.h) (`SERVO_TILT_MIN` 30°,
  `SERVO_TILT_MAX` 85°) exist to protect the neck from hitting its mechanical
  stops. If the head geometry changes, update those constants in the same pull
  request.

---

## Licence

Everything in this folder is licensed under the
[CERN Open Hardware Licence v2, Strongly Reciprocal](../LICENSE-hardware) —
not the MIT licence that covers the project's software.
