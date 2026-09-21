# Marvin — Mechanical

CAD sources and printable meshes for Marvin's chassis, tracks, and head.

```
hardware/mechanical/
├── src/        CAD models (Rhino 3D, .3dm)
├── step/       Whole-robot STEP, for other CAD packages
├── stl/        Meshes exported for printing
└── README.md   You are here
```

---

## Parts

Parts are numbered in print order: the PLA parts first — chassis, track
covers, wheels, then the head — and the one TPU part last.

| # | Part | File | Qty | Material |
| --- | --- | --- | --- | --- |
| 01 | Body / chassis | `01_body.stl` | 1 | PLA |
| 02 | Track cover, left | `02_track_cover_left.stl` | 1 | PLA |
| 03 | Track cover, right | `03_track_cover_right.stl` | 1 | PLA |
| 04 | Wheel | `04_wheel.stl` | 4 | PLA |
| 05 | Head base | `05_head_base.stl` | 1 | PLA |
| 06 | Head cover | `06_head_cover.stl` | 1 | PLA |
| 07 | Neck | `07_neck.stl` | 1 | PLA |
| 08 | Neck mount | `08_neck_mount.stl` | 1 | PLA |
| 09 | Track link | `09_track.stl` | many | TPU |

The eight PLA parts are 01–08; the track link is the only part in TPU, so it
sits last.

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
| Layer height | 0.08 mm |
| Walls | 3 |
| Infill | 20 % (structural parts), 40 % (neck and head mount) |
| Supports | Tree on the body (01) and neck (07); normal on the wheels (04) |

The track links are the one part that genuinely needs a flexible filament —
printing them rigid gives you a robot that cannot corner. If you print a set,
please open an issue with your material, printer, and what worked.

---

## Whole-robot models

Two files carry the complete assembly — chassis, head, tracks, motors, servos,
electronics — rather than a single printable part:

| File | Size | For |
| --- | --- | --- |
| `src/marvin_design_v3_hq.3dm` | 74 MB | Rhino, native |
| `step/marvin_robot.step` | 28 MB | any CAD that reads STEP AP242 |

Both come from the 930 MB Rhino working model. Most of that bulk was not real
NURBS: the heavy objects are degree-(1,1) 2x2 patches, i.e. flat triangles
wearing a NURBS costume, inherited from the cinematic mesh the design started
from. Converted to actual meshes they are ~100x cheaper and geometrically
identical — `02_track_cover_left` alone is 147 MB as a Brep and 1.3 MB as a
mesh. Genuinely modelled parts (`07_neck`, `08_neck_mount`, bearings, battery,
and 814 smaller objects) are still NURBS and still editable.

Accuracy against the source is 0.0000 mm on the converted parts and never worse
than 0.05 mm anywhere in the STEP.

**A caveat on the STEP.** It uses AP242 *tessellated* geometry, which is what
makes 700k triangles fit in 28 MB — as a B-rep the same mesh would be about
700 MB, because a STEP face costs ~1 kB whether it holds a NURBS patch or a flat
triangle. Verified to open in OpenCASCADE and FreeCAD. **Rhino 8 does not read
tessellated STEP** — Rhino users should take the `.3dm`, which is the same
geometry.

## Large files

One file exceeds what GitHub will accept and is **not in this repository**:

| File | Size | Why it is excluded |
| --- | --- | --- |
| `src/marvin_design.3dm` | 2.7 GB | Over GitHub's 100 MB file limit, and over Git LFS's 2 GB per-file cap |

It is listed in [`.gitignore`](../../.gitignore). To get it, ask for a copy — or,
better, help fix the underlying problem.

**The problem was tessellation, not complexity.** `01_body.stl` used to be a
454 MB export — roughly nine million triangles for a part that is essentially a
printed box — and had to be distributed out of band. Re-exported from Rhino at
a coarser mesh tolerance it is 240 k triangles and 12 MB, with no visible
detail lost at print resolution, so it now lives in the repository like every
other part. The whole `stl/` set is 22 MB.

Every mesh in `stl/`, the `.3dm` models and the `.step` export are tracked
through Git LFS. Install LFS before cloning or you will get pointer files
instead of geometry:

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
- **Rhino's ordinary Save re-adds the render-mesh cache** — it took the 74 MB
  model back to 142 MB in one open-and-save. Use `SaveSmall` when committing.
- Keep the numeric prefixes and update the table above when parts are added,
  renamed, or renumbered.
- Servo travel limits in the current
  [`firmware/`](../../firmware/README.md) protect the neck from hitting its
  mechanical stops. If the head geometry changes, update and physically verify
  those limits in the same pull request.

---

## Licence

Everything in this folder is licensed under the
[CERN Open Hardware Licence v2, Strongly Reciprocal](../../LICENSE-hardware) —
not the MIT licence that covers the project's software.
