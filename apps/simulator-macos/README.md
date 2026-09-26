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

Drive through the five amber beacons in order. The minimap shows the current
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

This is a **flat-ground kinematic simulation**, not a calibrated digital twin:

- Differential drive with acceleration, braking, and circular-footprint collision
  checks against the course's rectangular obstacles and boundaries. Fixed-size
  integration substeps prevent wall tunneling; delayed frames are clamped.
- Head articulation centers are inferred from the CAD, with yaw ±80° and pitch
  ±45° matching the firmware's logical bounds. CAD-derived oriented collision boxes conservatively stop pan and tilt before
  the head intersects the chassis. They approximate shell geometry; the neck
  joint and deformable parts are not collision meshes.
- Wheels have rotating hub markers. The source wheels retain their original shape.
  For a fuller appearance, each rubber track is widened 20% about its center,
  with a 6% taller and 4.5% longer profile, keeping its ground contact height.
  These visual proportions are not measured hardware dimensions; track-belt
  deformation is not modeled.
- White curved eye strokes sit on the original CAD front panel and blink.
  The neck is shell-colored; the five-button row uses one red and four pale
  buttons, with a separate pale round button. Materials are curated for the simulator. The source electronics layout is not verified against today's robot.
- No gravity, slopes, traction, deformable tracks, motor electrical model,
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
