# BB-8

- **Model:** [BB8](https://sketchfab.com/3d-models/bb8-6aff787c459a4e00a26ed11ac8f148a1)
- **Author:** Willy Decarpentrie (skudgee)
- **License:** [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/)
- **Pinned GLB mirror:** https://raw.githubusercontent.com/cortiz2894/hologram-particles/26f9cc1c16a51df2b67b4e5caaa50a6fc531c2dc/public/glb/bb8.glb
- **SHA-256:** `c813327f3abdb1e4823e7ff217a7e93d77b8d02dc1860f92fe8af151723955f4`

Source GLB metadata also identifies the original creator, model URL and license.
Conversion retains the original 7,198 triangles and textures, separates the ball
from the head, normalizes the scale, and adds simulator-driven movement and dirt.
The head mount pivots around the shell center for a contact-preserving head cock;
independent yaw anticipates bends and briefly follows passing rivals.
Run `scripts/export-racers-simulator.py` to regenerate the offline SceneKit data.

## Scale and movement references

[The official Star Wars Databank](https://www.starwars.com/databank/bb-8) gives
BB-8's height as **0.67 m**. The full mesh, including antennas, is scaled to that
height, using the same scene-to-metre scale as 1.08 m R2-D2 and 0.60 m Marvin.

[Lucasfilm's production account](https://www.starwars.com/news/droid-dreams-how-neal-scanlan-and-the-star-wars-the-force-awakens-team-brought-bb-8-to-life)
provides the reference for the rolling spherical body and independently balanced
head. In this simulator the ball rolls by displacement divided by its mesh radius;
the head stays above it and turns to face travel. It stops rolling when airborne.
Dirt builds across the rotating shell while the head receives lighter dust.
The single 0.045-scene-unit ground mark is an artistic contact-patch approximation,
not a measured tire width or a fictional wheel hidden in the ball.

Character and franchise rights remain with their respective owners; the mesh
license does not imply endorsement or transfer those rights.
