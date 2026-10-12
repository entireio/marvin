# Dune sand contact

Robots crossing dunes now deform the opaque ground around their running gear. The implementation preserves shallow asymmetric burial, cuts a compacted trough, pushes part of the removed sand into irregular side banks, and lets loose banks settle. It does not turn solid sand transparent to hide intersections.

## Approach

The shared `SandDeformation` field runs at 30 Hz only around dune contacts. Each robot uses its own footprint: tracks for Marvin and WALL-E, three wheel contacts for R2-D2, and one small contact for BB-8. Exposed contacts neither cut the sand nor emit spray. Driving, reversing and pivoting use the same contact calculation.

Touched terrain receives 4 m replacement patches at 6.25 cm spacing. Original terrain triangles are removed underneath these patches, so ruts cannot reveal an undeformed second surface. The render mesh and collision sampling use the same triangle diagonal and height data. Immutable Metal height textures drive vertex displacement and normals; the vertex and index buffers remain static. Changes below 0.5 mm accumulate before another texture upload.

A maximum of 64 patches bounds terrain memory and work. Old patches may be reclaimed beyond an 8 m protection radius around every robot. Recent banks receive conservative neighbor exchanges; old tracks stop consuming settling work. History is local and finite, not a permanent deformation of the entire planet.

Dune chassis orientation now composes the local support plane with heading. The former Euler order applied bank in world axes and could tilt a diagonal robot away from the slope it had sampled. Physics uses each robot's actual support dimensions. A rough, sand-colored coating softens the visible running-gear contact edge while remaining opaque. The track and town materials retain their existing behavior.

This is a bounded heightfield approximation, not a particle-level granular solver. Compaction absorbs part of the displaced volume; the remaining loose sand forms banks. It does not model excavated tunnels, deep bogging, or permanently accumulated dunes from every past lap.

## References

- [MudRunner's developer explanation](https://www.gamedeveloper.com/programming/mud-and-water-of-spintires-mudrunner): combine local terrain deformation, surface detail and particles at separate scales.
- [Batman: Arkham Origins deformable snow presentation](https://colinbarrebrisebois.com/wp-content/uploads/2022/06/gdc2014-deformable_snow_rendering.pdf): runtime heightfields and actual displaced geometry.
- [Journey rendering presentation](https://advances.realtimerendering.com/s2012/index.html): dune shape, shading and sand appearance.
- [Apple SCNShadable documentation](https://developer.apple.com/documentation/scenekit/scnshadable): geometry shader modifiers and custom texture bindings.

## Reproduction

Build with `apps/simulator-macos/build-app.sh`. Run the app binary with `--dune-contact-test OUTPUT_DIRECTORY` for four-robot diagonal driving, stopping, reversing, pivoting, low-angle camera sweeps, and reset checks. `MARVIN_DUNE_MOVIE=1` also captures an eight-second close-up sequence. `MARVIN_DUNE_LIVE=1` runs the same stress fixture in real time; its four independent physics instances make it more demanding than a normal race.

The `SimulationChecks` executable covers bounded cuts and banks, support contact, quaternion orthogonality, exact GPU snapshot data, tile boundaries, cache eviction around protected robots, and reset. The existing debris smoke test checks soil-dependent colors and air/contact emission behavior.

For normal gameplay timing, use `--town-benchmark OUTPUT_DIRECTORY --dune-roam`. `--benchmark-no-deformation`, `--benchmark-no-contact-coating` and `--benchmark-no-sand-shadows` are diagnostic comparisons, not player settings. Render timing is measured from SceneKit callbacks; it is not a GPU timestamp or display-presentation measurement.

The changing contact plane and accumulated dirt amount share a two-texel state texture per robot. This avoids invalidating shader uniforms on every articulated part. CAD geometry without texture coordinates gets a zero UV stream for SceneKit's textured shader pipeline. Native image checks verify that Marvin remains opaque and that toggling the coating changes pixels only near WALL-E's running gear.

Texture storage uses shared memory on Apple-family GPUs and managed storage on other Mac GPUs, following [Apple's storage-mode guidance](https://developer.apple.com/documentation/metal/mtlstoragemode/shared). Resources are immutable after upload, so render work cannot read a CPU-overwritten buffer or texture.

## Validation results

Measured on Apple M2 at a 1920 × 1080 drawable, clear weather, with all effects active. Activity Monitor was paused during timing and restored afterward.

| Fixture | Duration | Mean render callbacks/s | p95 interval | Intervals >25 ms / >50 ms | p95 CPU update |
|---|---:|---:|---:|---:|---:|
| Normal dune route | 45 s | 59.99 | 18.18 ms | 0 / 0 | 5.57 ms |
| Four robots deforming dunes simultaneously | 30 s | 60.00 | 22.55 ms | 1 / 0 | 8.37 ms |

The stress fixture reached 47 patches, about 3.49 cm of compaction and 2.14 cm of raised banks. The cache test forced 324 allocations, stayed within 64 resident patches, and retained terrain beneath the protected robot. The full simulation suite passed, including existing race, storm, gate, service-area and collision checks. These results are callback-cadence measurements, not proof of every displayed frame meeting its presentation deadline.
