# WALL-E

- **Model:** [Wall-E(Animated)](https://sketchfab.com/3d-models/wall-eanimated-a6758de2e5a04f9e821596592ef4279c)
- **Author:** Janis Zeps (Zeps3D)
- **License:** [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/)
- **Pinned GLB mirror:** https://raw.githubusercontent.com/crazygl-com/hero-wall-e/354a5ccbab5e1724e98b02799c8148dd4de8f715/models/wall-e.glb
- **SHA-256:** `64f568ccf91098d18843a033c7d6ba770a6803b25f85a533e1957d03d5424b0e`

Source GLB metadata and the mirror's README identify the original creator and
license. Conversion retains the original 40,158 triangles and textures, splits
the left/right running gear, retimes the authored track animation by signed
travel, normalizes scale, and adds simulator-driven motion and surface dirt.
The original arm/gripper meshes are grouped under their shoulder pivots for a
lowered driving pose, turn-indicating gestures and brief passing greetings.
Context-driven head yaw, tilt and roll anticipate bends and acknowledge rivals.
Run `scripts/export-racers-simulator.py` to regenerate the offline SceneKit data.

## Scale and movement references

**Height used: 1.016 m (3 feet 4 inches), ground to top of the eyes.** This is a
reported production measurement, not an official published Pixar specification:
[a January 2009 firsthand builder report](https://walle-meta.livejournal.com/10488.html)
records a call with Pixar's publicist and explicitly describes this measurement.
[Pixar's character page](https://www.pixar.com/wall-e) supplies the visual reference.
The mesh is scaled to that height using the same scale as the other racers.

The source has an authored 5.25-second closed tread cycle (253 poses at 48 fps).
Each side advances independently by signed simulated track travel divided by
measured belt-loop length. Braking holds the links; reversing reverses them;
turning drives the sides at different rates. Track impressions use each belt's
actual mesh width and lateral position. Lower tracks, chassis and wheel-adjacent
surfaces collect most dirt; upper eye surfaces retain much lighter dust.

As with the other racers, acceleration, grip and AI pace are game tuning rather
than claimed canonical physical specifications.
Character and film rights remain with their respective owners; the mesh license
does not imply endorsement or transfer those rights.
