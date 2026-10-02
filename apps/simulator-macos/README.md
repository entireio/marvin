# Marvin Simulator for macOS

Agent contributors: read the [simulator implementation and validation rules](../../docs/simulator-agent-rules.md) before changing the simulator.

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
the app, with a spinner and current stage pinned above grey process output.
Redirected output stays plain text. Control-C cancels the build.
Model exports are cached in `.build/asset-cache`: unchanged sources and exporter
code skip regeneration, while missing or modified generated files trigger it.
The first build populates this cache; deleting it forces a fresh export.
You can copy `.build/Marvin Simulator.app` to Applications after building.
The local build is ad-hoc signed, not notarized for distribution. It targets the
Mac's current architecture; a build on Apple Silicon produces an arm64 app.

The build script uses an installed standalone Command Line Tools toolchain if
the selected Xcode toolchain is unavailable. With standalone CLT it selects
SwiftPM's native build backend, avoiding the default Swift Build backend's
missing Xcode-only search paths and legacy `arclite` warning. Full Xcode builds
keep the default backend. The build and binary-path lookup use the same backend.
Swift 6.4 currently emits a deprecation notice for the native backend; this
workaround keeps that notice visible rather than suppressing diagnostics. Once
the selected full Xcode installation is usable, the fallback is unnecessary.
This only affects the build process; it does not change `xcode-select` or accept
any license agreements. Model-export summaries and codesign's “replacing
existing signature” message are normal successful-build output.

## Main menu

The app opens with a live 3D Marvin portrait. Use the cursor keys and Return,
or hover and click, to select Sandbox, Dirt Track, Settings, or Quit. Drag left or
right on the portrait to rotate Marvin; he stays at the angle you release.
Hidden main-menu shortcuts: **B** shows BB-8, **R** shows R2-D2, **W** shows
WALL-E, and **M** restores Marvin. Each portrait supports dragging and idle
head movement. Your choice carries into Sandbox and Dirt Track and survives
restarts during this app session. The other three robots become your race rivals.
Marvin looks around
and blinks at random intervals. Settings saves the idle-animation, keyboard-guide,
**Robot collisions**, **Steering assist**, and **Braking assist** preferences. Robot collisions are on by default; turn them
off to let all four Dirt Track racers pass through one another. Track barriers,
terrain, jumps, and sandbox collisions still work as usual.
Use the Main Menu toolbar button or Command-M to return from the sandbox;
entering Sandbox starts a fresh course.

## Dirt Track

Wheels, tracks, and BB-8's shell keep spinning under drive during jumps and
against fences or sandbox obstacles. Barriers stop the chassis, not its running
gear. Braking stops drivetrain spin; pausing freezes it and restarting resets it.

**Steering assist** and **Braking assist** are on by default and can be switched
off independently in Settings. They apply only to the player during Dirt Track:

- Keep pressing A/D or the arrow keys to steer. Steering help refines a turn
  toward a smooth racing line while preserving your chosen direction. Help fades
  smoothly as you steer away from the line at an exit. Releasing steering gives
  no line-following correction; the assist never reverses your chosen turn.
- Hold Space to brake before a curve. Braking help keeps full braking strength
  and lets you keep steering through the curve. Holding Space keeps
  slowing you down; release it and use the throttle to accelerate out.
- Neither assist drives for you, saves an unbraked boosted corner, or acts in
  the air or in reverse. Steering guidance also disengages outside the lane or
  while facing the wrong way. Sandbox controls and AI drivers are unchanged.
  Disabling both restores the original manual controls.

The design draws on the separate steering/braking aids documented for
[F1 22](https://www.frostbite.com/able/resources/f1-22/pc/assists) and the distinction
between stability assistance and Auto-Drive in
[Gran Turismo 7](https://www.gran-turismo.com/au/gt7/manual/drivingoption/03)
(reviewed September 28, 2026). Unlike their more automatic modes, these assists
require the corresponding player input. The internal reference line is smoothed
within the lane, and guidance follows actual chassis speed even when wheels slip.
It is an arcade aid, not a mathematically optimal racing line.

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
a course map, and local best race totals. The finish panel ranks all four robots by total race time and shows each best lap.
Unfinished racers show their current lap until their actual finish time is recorded.
Finishers continue at a slower cooldown pace; after Marvin finishes, autopilot
takes over and the camera reverses the opening flight back to the overview.
Race times freeze individually at the finish. Pause freezes both the procession
and camera flight, and restart restores player control.
The timer uses monotonic elapsed time, excluding countdown, pauses, and time while the app is inactive; slow frames do not improve scores. Lap
crossings interpolate within a frame; signed course progress prevents reverse
finish crossings from awarding laps. Loose shoulders reduce grip; the fence and other robots block Marvin, with sliding contact so he can steer or reverse away. The scene is prepared before revealing a 3.2-second overview-to-chase camera flight. Countdown and driving wait until the flight completes. The default chase camera looks directly along the driving direction. Steering ramps in over a third of a second and caps moving turns at approximately 66 degrees/second independently of drive/boost speed. Dirt Track speeds are 6 units/s normally and 12 with boost (three times the original mode).
Command-R starts a fresh race. The Main Menu toolbar button returns to the menu.

Race against autonomous **R2-D2, BB-8 and WALL-E**. All four racers are randomly
assigned distinct staggered starting boxes on every new race and Command-R reset.
The AI steers toward look-ahead points in separate lanes, slows for turns, and
boosts on straights using the same acceleration, steering, fence, terrain and jump
simulation. The HUD shows position out of four, every opponent's lap, and map
markers: Marvin silver, R2-D2 blue, BB-8 orange, WALL-E yellow. Markers stay on the
course polyline. Three-lap finish order determines position; local scores record
your time. All racers wait for the fly-in/countdown, freeze on pause or app
inactivity, and reset together. Robot-to-robot contact now transfers momentum,
causes sideways skids and off-center yaw rotation, and prevents interpenetration.
AI drivers retain their lane-following strategy; they can bump and push each other.

Race acting looks ahead into the next bend before the chassis turns. When a
rival enters the nearby passing zone, the robot briefly looks toward it, then
returns its attention to the course. A short cooldown prevents repeated staring
at the same neighbor. Marvin gives a focused glance and small acknowledging nod;
R2-D2 makes deliberate dome swivels; BB-8 cocks its independently balanced head;
WALL-E tilts his binocular head and raises the inside hand to indicate a turn,
with a brief greeting toward a passing robot. His source-model raised arm rests
in a lowered driving pose between gestures. These are authored character
performances, driven by race context rather than a repeating idle animation.
Manual Marvin head controls remain additive and respect the CAD clearance limits.
All acting follows simulation time, freezes on pause/focus loss, and resets with
the race. Sandbox head controls and menu animation are unchanged.

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

Model credits and licenses:

| Model | Creator | License | Bundled attribution and modifications |
| --- | --- | --- | --- |
| [R2-D2](https://sketchfab.com/3d-models/r2-d2-9e6b5bc13f7943d08e657bffce78fc90) | LordDiego | [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/) | [R2-D2 attribution](Resources/R2D2/ATTRIBUTION.md) |
| [BB8](https://sketchfab.com/3d-models/bb8-6aff787c459a4e00a26ed11ac8f148a1) | Willy Decarpentrie (skudgee) | [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/) | [BB-8 attribution](Resources/BB8/ATTRIBUTION.md) |
| [Wall-E(Animated)](https://sketchfab.com/3d-models/wall-eanimated-a6758de2e5a04f9e821596592ef4279c) | Janis Zeps (Zeps3D) | [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/) | [WALL-E attribution](Resources/WallE/ATTRIBUTION.md) |


The fastest ten complete races persist atomically in
`~/Library/Application Support/Marvin Simulator/motocross-v4-scores.json`.
Earlier scores remain in `motocross-v2-scores.json` and `motocross-v3-scores.json`;
the new collision/traction simulation uses a separate leaderboard. Invalid records are excluded; load failures preserve the existing file and show
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
Ground effects allow 0.006 simulation units of rubber/soil contact tolerance, so
millimeter-scale hops do not repeatedly tear the trail. Racers clearly off the
ground leave no marks or soil spray; restarting clears all trails. All robots
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

All four racers share a **240 Hz upright rigid-body contact solver**. Oriented
box footprints for Marvin, R2-D2 and WALL-E, plus a circular footprint for BB-8,
follow the scaled model bounds. Vertical overlap distinguishes side impacts,
landing on another robot, and passing clear overhead. Sequential impulses use
mass and yaw inertia, low restitution (0.08), and Coulomb contact friction (0.45).
Separate mass-weighted penetration correction avoids adding bounce energy and
iterates with fence/ground constraints to resolve pileups. Drive force, lateral
grip and steering torque are bounded; brakes slow the body over distance instead
of deleting momentum. Wheel/track travel still follows the drivetrain, allowing
visual wheel slip during impacts. Pause, countdown and reset gate the whole group;
lap progress is evaluated after contact resolution.

Collision masses are **gameplay estimates**, not sourced character specifications:
Marvin 18 kg, R2-D2 55 kg, BB-8 12 kg and WALL-E 85 kg. Friction, restitution and
motor forces are also tuning values. Collision hulls approximate the solid body;
individual antennas, fingers, moving heads and tread links are not separate rigid
bodies. Translation has three axes and collision rotation is yaw; terrain still
controls pitch/roll. Full tipping, tumbling, suspension and shell deformation are
not simulated.

Collision spin recovery intentionally adds a small arcade assist: after a contact
spins a robot out, yaw eases back toward the forward course tangent over roughly
three seconds. It does not teleport racers or add forward speed. Core checks
cover recovery, continued AI driving after finishing, frozen finish times, and
classification ordering; native smoke captures start, pause and results overlays.

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

Sandbox obstacle and wall collision uses Marvin’s scaled body footprint, including
its orientation, with 0.006 units of clearance. Route planning uses its enclosing
circle conservatively.

The sandbox is a **flat-ground kinematic simulation**; Dirt Track adds terrain following, ballistic jumps and coupled robot contact dynamics. Neither is a calibrated digital twin:

- Differential drive with acceleration, braking, and scaled oriented-footprint collision
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
- Marvin’s body and head use a matte silver metallic finish inspired by the
  1950s Silver Arrows, with matching silver map and standings markers.
- White curved eye strokes sit on the original CAD front panel and blink.
  The neck is shell-colored; the five-button row uses one red and four pale
  buttons, with a separate pale round button. Materials are curated for the simulator. The source electronics layout is not verified against today's robot.
- No calibrated traction, deformable tracks, motor electrical model,
  sensor emulation, firmware connection, or robot commands.

## Verification

For a focused native check of menu shortcuts, portrait rotation, and navigation:

```sh
'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' \
  --menu-smoke-test /tmp/marvin-menu-smoke
```

Use `--character-smoke-test /tmp/marvin-character-smoke` instead to check every
playable robot in both modes, including driving, resets, race lineup, HUD names,
model positions, and trails. This also captures each robot in both scenes.

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
finish timing, terrain containment, jumps, cooldown driving and reset. Native smoke also
checks all imported models and opponent motion; `dirt-grid.png` captures the start.
The grid checks cover every AI lane and slot at all three frame rates.

Native smoke captures `r2d2-front.png` and `r2d2-wheels.png` for model inspection
and verifies forward/reverse wheel rotation, braking, ground contact and reset.
`bb8-front.png`, `walle-front.png` and corresponding `*-dirty.png` captures check
new model appearance. `acting-neutral.png`, `acting-curve.png` and
`acting-passing.png` show all four characters with their articulated poses.
Attention checks cover both turn/passing directions, cooldown, parked behavior,
frame rates, pause and reset. Node checks verify sphere rolling direction, independent
tread movement, braking, reverse, scale and reset; all four dirt emitters and
trail histories are checked for emission, airborne behavior, pause and cleanup.
The native smoke test also drives a complete three-lap race through the coupled
physics and trail renderer. `full-race-trails.json` reports lap times, mark counts,
small-hop contact, actual flights, missing grounded marks, and ray-cast checks
against the rendered lane; `full-race-trails.png` captures the resulting course.

Robot-contact checks cover momentum and energy, unequal masses, off-center spin,
rounded corners, overhead clearance, vertical landing, boosted opposing impacts,
coincident centers, fence pileups, finite braking, and exact fixed-input results
at 10/30/60/120 fps. Coupled three-lap races exercise all four bodies at
30/60/120 fps. Native smoke runs a crowded contact scenario through the real race
controller and captures `robot-contact.png`, including separation and pause checks.

## Spaceport city

Dirt Track sits inside **Mos Aster**, a dense procedural spaceport inspired by
Mos Eisley and Mos Espa film stills. The city extends beyond both overview cameras:
1,327 building modules form joined adobe compounds, terraces, courtyard workshops
and domed halls. Branching streets connect the market, hangar and circular docking
courts; there is no rectangular perimeter road or isolated mesa ring. Worn city
paving covers the scenery ground while the original dirt course stays intact.

The finish-line grandstand is built into a populated market arcade. There are
298 spectators/residents, four market stalls, parked speeders and rooftop utility
hardware. Two open infield repair tents contain mechanics, robot lifts, benches,
tools and spare parts. Houses are excluded from the entire circuit interior.
Fifteen nearby people have bounded idle animation (hard cap 16). The old sliding
plaza walkers are replaced by stationary, posed residents.
Race geometry, lap rules, robot contacts and driving assists are unchanged.

Static architecture uses 130 spatial batches and near/far geometry tiers. Rounded
adobe edges, smoother domes, layered arched door frames, scanned 2K plaster,
cloth and metal surfaces, sagging canopies, and HDR sky lighting improve close-up
appearance. The 298 people use clothed human meshes with modeled faces, hands,
boots and hoods, batched into small cells with three independent detail levels.
The city keeps its previous layout and the muted clay course color.

Race branding, grandstand gates/sectors, repair tents, market stalls, workshops,
and street wayfinding share a mounted enamel-sign system. Text is fitted in both
dimensions and centered within a measured content area; signs have directional
orientation, frames and mounting hardware. They are rasterized once, not laid out
or billboarded during the race.

Normal builds use bundled assets, with no downloads or extra asset tools. Run
`git lfs pull` after cloning. For offline human-mesh editing, see
[the crowd exporter](../../scripts/city/README.md). Credits and CC0 source links
are in [City/ATTRIBUTION.md](Resources/City/ATTRIBUTION.md).
The chase/orbit camera shortens against scenery bounds. Crowd motion respects
pause/focus gates. See [film comparison and validation](../../docs/dirt-city.md).

Dirt Track uses 2× MSAA, a 2048² sun shadow map and restrained screen-space ambient
occlusion (disabled on return to Sandbox). Dirt debris uses two batched meshes;
robot coating uploads remain quantized and spread across frames to avoid redundant
Metal buffer updates, without changing logical dirt accumulation.

Capture the native scene and validate track clearance, infield exclusion, city
coverage, branching street connectivity, geometry budgets, camera obstruction,
crowd pause and reset behavior:

```sh
'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' \
  --town-smoke-test /tmp/marvin-town
```

Run a real-time driving/overview benchmark (default 45 seconds, first three
seconds excluded from timing):

```sh
MARVIN_BENCHMARK_SECONDS=180 \
  'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator' \
  --town-benchmark /tmp/marvin-town-benchmark
```

Add `--without-town` for an A/B run with the same course and scenery camera
bounds, but hidden town and no crowd updates. Both runs use a fixed starting grid,
autonomous player input, and a 960×540-point view (1920×1080 drawable pixels on a
2× display). The report records actual drawable dimensions, CPU update p95,
thermal state, and SceneKit render-callback intervals. Render callback timing is
not GPU execution time or display presentation timing. Run benchmarks individually
without builds or other profiling workloads. Smoke tests use fixed simulation
steps and are not frame-rate benchmarks.

V1 uses stylized procedural art and simple crowd poses. General pedestrian
navigation, explorable interiors, and dynamic traffic are not implemented.

### Experimental RealityKit comparison

The normal game still uses SceneKit. An opt-in `--renderer-study OUTPUT` mode
compares a fixed grandstand block with the same geometry and simulation; add
`--realitykit` for the alternate backend (macOS 15+ and an installed Apple SDK).
This is a migration study, not a complete playable port. See
[renderer study](../../docs/renderer-study.md) for measurements, screenshots,
known parity gaps, capture commands, and migration gates.
