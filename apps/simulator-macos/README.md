# Marvin Simulator for macOS

A native AppKit + SceneKit/Metal playground using Marvin's actual CAD assembly.
No browser, web server, npm dependencies, network connection, or physical robot
is needed. Requires macOS 13 or later. The build uses Python 3 and Apple's Swift
command-line tools; the resulting `.app` runs independently of those tools.

## Build and launch

From the repository root:

```sh
apps/simulator-macos/build-app.sh
open 'apps/simulator-macos/.build/Marvin Simulator.app'
```

Or double-click **Launch Marvin.command** in this directory. It builds and opens
the app. You can copy `.build/Marvin Simulator.app` to Applications after building.
The local build is ad-hoc signed, not notarized for distribution. It targets the
Mac's current architecture; a build on Apple Silicon produces an arm64 app.

The build script uses an installed standalone Command Line Tools toolchain if
the selected Xcode toolchain is unavailable. This only affects the build process;
it does not change `xcode-select` or accept any license agreements.

## Main menu

The app opens with a live 3D Marvin portrait. Use the cursor keys and Return,
or hover and click, to select Sandbox, Dirt Track, Settings, or Quit. Marvin looks around
and blinks at random intervals. Settings saves the idle-animation and keyboard-guide
preferences. Use the Main Menu toolbar button or Command-M to return from the sandbox;
entering Sandbox starts a fresh course.

## Dirt Track

A fixed, winding motocross circuit inspired by [Dirt Rider’s layout guide](https://www.dirtrider.com/understanding-motocross-track-layouts/):
a start straight and tabletop, banked mixed-direction turns, rollers, rhythm
jumps, a raised hill/step-up, and whoops. The route uses a closed spline and a
single continuous height surface. The compacted lane is now 50% wider (3.9 scene units) with the same centerline
and 136.256-unit plan-view lap length. Taller tabletop/rhythm jumps, larger
rollers and whoops, a 1.3-unit hill, and three additional broad climbs add
vertical variation. Course shoulders descend into the terrain outside the fence.
The red/white rail follows the ground with 0.31-unit center clearance; every
post extends 0.08 units below the ground and 0.40 above it. Inside offset loops
are trimmed at tight bends so the wider surface and fence do not cross themselves.

Select **Dirt Track**, watch the bird’s-eye fly-in, then wait for the three-second countdown, and complete **three
laps** in the marked direction. The HUD shows current-lap time, best lap, total,
a course map, and local best race totals. The finish panel lists all three splits.
The timer uses monotonic elapsed time, excluding countdown, pauses, and time while the app is inactive; slow frames do not improve scores. Lap
crossings interpolate within a frame; signed course progress prevents reverse
finish crossings from awarding laps. Loose shoulders reduce speed; only the fence blocks Marvin, with sliding contact so he can steer or reverse away. The scene is prepared before revealing a 3.2-second overview-to-chase camera flight. Countdown and driving wait until the flight completes. The default chase camera looks directly along the driving direction. Steering ramps in over a third of a second and caps moving turns at approximately 66 degrees/second independently of drive/boost speed. Dirt Track speeds are 6 units/s normally and 12 with boost (three times the original mode).
Command-R starts a fresh race. The Main Menu toolbar button returns to the menu.

Race against autonomous **R2-D2, BB-8 and WALL-E**. All four racers are randomly
assigned distinct staggered starting boxes on every new race and Command-R reset.
The AI steers toward look-ahead points in separate lanes, slows for turns, and
boosts on straights using the same acceleration, steering, fence, terrain and jump
simulation. The HUD shows position out of four, every opponent's lap, and map
markers: Marvin gold, R2-D2 blue, BB-8 orange, WALL-E green. Markers stay on the
course polyline. Three-lap finish order determines position; local scores record
your time. All racers wait for the fly-in/countdown, freeze on pause or app
inactivity, and reset together. Racing remains non-contact: robot-to-robot
collisions and avoidance are not implemented.

The bundled [R2-D2 model by LordDiego](https://sketchfab.com/3d-models/r2-d2-9e6b5bc13f7943d08e657bffce78fc90)
is a detailed 25,158-triangle textured model under CC BY 4.0. It uses the
original animation's deployed third-leg driving pose and smooth surface normals.
Four outer drive wheels and one center caster use documented Colson dimensions
from a replica-builder configuration ([wheel references](Resources/R2D2/WHEEL_REFERENCE.md)).
They rotate from
signed wheel travel, stop under braking/pause, and reverse with the drivetrain.
Marvin is scaled to 0.60 m relative to R2-D2’s 1.08 m height (55.6% as tall),
with matching tread travel and dirt-effect spacing. The dome gently turns on its inclined pivot. The source archives, conversion
credits and modification notes ship in `Resources/R2D2`; the exporter runs
offline without Blender. This is an artistic mesh, not engineering CAD.


[BB-8 by Willy Decarpentrie](Resources/BB8/ATTRIBUTION.md) retains 7,198 triangles
and is scaled to the official **0.67 m** height. Its spherical shell rolls by
actual ground displacement while its head stays upright and turns independently.
[WALL-E by Janis Zeps](Resources/WallE/ATTRIBUTION.md) retains 40,158 triangles.
His **1.016 m** height is based on a builder's firsthand report of a Pixar call,
not an official published specification. His two articulated tread loops and
gears follow signed left/right travel independently, including turns, braking,
and reverse. The binocular head subtly looks around. Both models are CC BY 4.0;
creator credits, pinned sources, measurement references and modification notes
ship with the app. Offline conversion uses Python's standard library.


The fastest ten complete races persist atomically in
`~/Library/Application Support/Marvin Simulator/motocross-v3-scores.json`.
Earlier course scores remain in `motocross-v2-scores.json`; the changed terrain
uses a separate leaderboard. Invalid records are excluded; load failures preserve the existing file and show
an error instead of overwriting it. Smoke tests use a separate temporary file.

Soil uses the CC0 [Poly Haven Dirt](https://polyhaven.com/a/dirt) scanned diffuse,
OpenGL normal, and roughness maps, combined with procedural compacted lanes and
ruts, warm sunlight, terrain normals, and distant haze. Attribution ships in
`Resources/Dirt/ATTRIBUTION.md`. Resources are bundled for offline use.
Track motion emits pooled world-space dust and gravity-driven clods in the
correct forward/reverse direction; clods bounce and settle. Grounded travel leaves
persistent terrain-following tread marks for Marvin and three continuous, smooth
tire impressions for R2-D2, one smooth rolling contact trace for BB-8, and wider
mesh-sized tread impressions for WALL-E. All four racers throw dust and clods. Marks are spaced by distance, including
at boost speed, and retained in bounded mesh batches for a full three-lap race.
Airborne racers leave no marks or soil spray; restarting clears all trails. All robots
also accumulate surface-attached dirt with distance driven: patchy mud around
running gear and lower panels, stronger wheel/rear spray exposure, and light
dust higher up. Coated areas become rougher and less reflective; restart cleans
the models. BB-8 collects dirt across its rolling shell while its head remains
relatively clean. This is a procedural visual treatment, not particle-level deposition.

Racers shuffle between four staggered, opposite-side grid boxes behind the finish line.
Lap progress starts at each grid position so the first start-line crossing cannot
count as a completed lap.

Terrain pitch/roll, crest launch and gravity are an **approximate game model**;
this is not calibrated granular-soil, suspension, or deformable-track physics.
Boost and obstacles are scaled for a playable robot-sized course.

## Controls

| Action | Keyboard / mouse |
| --- | --- |
| Forward / reverse | W / S or up / down arrows |
| Turn, including in place | A / D or left / right arrows |
| Faster drive | Hold Shift |
| Immediate brake | Hold Space |
| Head left / right | Q / E |
| Head up / down | R / F |
| Center head | H |
| Cycle follow, orbit, overview camera | C or Camera toolbar button |
| Orbit around Marvin | Drag |
| Zoom | Scroll / trackpad |
| Pause / resume | P, Escape, or Pause toolbar button |
| Reset position, head, course, telemetry, and camera | Command-R or Reset toolbar button |
| Show / hide controls | ? or Controls toolbar button |
| Quit | Command-Q |

Drive through the five amber beacons in order. Each launch or reset places a new
random course, keeping the full ring grooves clear of obstacles, the launch pad,
the arena edges, and each other. The rings are recessed V-shaped floor grooves;
the active groove pulses in brightness without changing its size. Large centered
numbers face the final approach of the shortest route from launch or the preceding
checkpoint, using conservative obstacle polygons padded for Marvin’s footprint. The minimap shows the current
objective, obstacles, and Marvin's heading. Driving into an obstacle stops
translation; turn or reverse to get free. Input and drive velocity clear when
the window loses focus. The simulation freezes while the app is inactive.

## Model and simulation boundaries

`scripts/export-marvin-simulator.py` converts the repository's AP242 STEP file
into a packed mesh bundle using only the Python standard library. It preserves
all 23 component groups and 695,172 triangles in their assembly coordinates and
records the source SHA-256. One scene unit represents 100 mm. The original CAD
is not modified. Generated resources are ignored and rebuilt automatically.
Hardware geometry remains under [CERN-OHL-S-2.0](../../LICENSE-hardware).

The sandbox is a **flat-ground kinematic simulation**; Dirt Track adds terrain following and simple ballistic jumps. Neither is a calibrated digital twin:

- Differential drive with acceleration, braking, and circular-footprint collision
  checks against the course's rectangular obstacles and boundaries. Fixed-size
  integration substeps prevent wall tunneling; delayed frames are clamped.
- The pan axis follows the CAD neck ring center (Z = −1.886 mm); the pitch
  pivot is inferred from the assembly, with yaw ±80° and pitch
  ±45° matching the firmware's logical bounds. CAD-derived oriented collision boxes conservatively stop pan and tilt before
  the head intersects the chassis. They approximate shell geometry; the neck
  joint and deformable parts are not collision meshes.
- Each track has 56 circulating tread shoes over a continuous rubber belt.
  Belt phase follows that side's signed travel, including reverse and pivot turns;
  the bottom run moves backward relative to the chassis when driving forward.
  The original static CAD tracks are hidden. Animated belts approximate their
  widened profile; these are visual proportions, not measured hardware dimensions.
  The rubber meets the ground at zero chassis lift. The dock is a thin floor inlay.
- White curved eye strokes sit on the original CAD front panel and blink.
  The neck is shell-colored; the five-button row uses one red and four pale
  buttons, with a separate pale round button. Materials are curated for the simulator. The source electronics layout is not verified against today's robot.
- No calibrated traction, deformable tracks, motor electrical model,
  sensor emulation, firmware connection, or robot commands.

## Verification

```sh
# Use DEVELOPER_DIR only if the default Xcode selection is unavailable.
DEVELOPER_DIR=/Library/Developer/CommandLineTools \
  swift run --package-path apps/simulator-macos SimulationChecks

# Launch the native renderer, exercise input, capture a frame, and exit.
'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' \
  --smoke-test /tmp/marvin-simulator-smoke
cat /tmp/marvin-simulator-smoke/smoke.json
```

Core tests cover forward/reverse, braking, steering, collisions, checkpoint
progress, pause/reset, head limits, frame-rate consistency, and delayed frames.
The native smoke test checks mesh loading, drive/turn input, and captures a real
SceneKit frame. Inspect `smoke.json` for `passed: true` and the image for rendering.

The isolated 3 mm solid on the CAD layer `Head` is hidden in the simulator;
the actual head shells remain visible. The original CAD and imported mesh data
are preserved.

## App icons

The existing Entire vector symbol is preserved in light and dark rounded-tile
icons. The running app's Dock icon follows macOS effective appearance and updates
when it changes. Finder and the app before launch use the light ICNS fallback.
Both multi-resolution ICNS files and 1024px PNG/SVG artwork live in
`Resources/Icons`. To regenerate them, run `python3 scripts/make-marvin-app-icons.py`
from the repository root (requires `rsvg-convert` and macOS `iconutil`).

Dirt checks cover a complete three-lap driven race with jumps, boundaries, reverse
finish crossings, split timing, score sorting and disk round trips. Native smoke
checks mode switching, rendering, debris emission, score recording, and reset;
`dirt-overview.png` and `dirt-driving.png` capture the new scene.

Opponent checks run complete three-lap AI races at 30, 60 and 120 fps, checking
finish timing, terrain containment, jumps, stopping and reset. Native smoke also
checks all imported models and opponent motion; `dirt-grid.png` captures the start.
The grid checks cover every AI lane and slot at all three frame rates.

Native smoke captures `r2d2-front.png` and `r2d2-wheels.png` for model inspection
and verifies forward/reverse wheel rotation, braking, ground contact and reset.
`bb8-front.png`, `walle-front.png` and corresponding `*-dirty.png` captures check
new model appearance. Node checks verify sphere rolling direction, independent
tread movement, braking, reverse, scale and reset; all four dirt emitters and
trail histories are checked for emission, airborne behavior, pause and cleanup.
