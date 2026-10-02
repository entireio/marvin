# Simulator implementation and validation rules

This is the standing guide for agents working on the macOS simulator and Mos
Aster. Read it before planning, implementing, testing, or declaring work complete.

## Source, scope, and precedence

These requirements consolidate the user's instructions and corrections available
in the conversation **“Check out entireio/marvin repo”**, including the earlier
work sessions preserved there, through **2026-10-02**. Source thread/session:
`01a0e98d-2524-7cf0-b87e-b6ce0dcf249c`. This is a deduplicated requirements record,
not a verbatim transcript or a claim to have inspected every separate conversation.
Questions that exposed defects are captured as the resulting implementation or
verification requirement. Brief approvals such as “make it so” inherit the task
they approved; they do not grant unrelated permissions.

- New explicit user instructions take precedence. When they change a standing
  requirement, update this guide rather than retaining contradictory rules.
- Later corrections supersede rejected versions. Preserve the user's intent,
  not an earlier agent's particular implementation or claim of completion.
- The product requirements below come from the user. The validation procedures
  translate those requirements and observed failures into actionable checks;
  they are not all verbatim user instructions.
- Existing implementation notes are evidence and context, not additional user
  mandates. They contain historical and superseded designs. Inspect current
  code and actual runtime behavior before relying on them.
- No rule here overrides higher-priority instructions, applicable permissions,
  or the user's current scope. Do not turn historical requests into unrelated
  work or automatically kill applications in a later session.

## 1. Working standard and completion

The user repeatedly asked for cinematic quality, “much more” realism, continued
iteration, and comparison with real references. A successful build, plausible
procedural geometry, or a passing performance benchmark is not visual acceptance.

1. Investigate the existing simulator, reproduce the issue, and do the authorized
   work. Do not stop at a plan or offer to continue after approval to implement.
2. Research before inventing visual, acoustic, physical, or driving behavior when
   the task calls for real-world or film resemblance. Actually inspect the
   relevant images, footage, or documentation.
3. Implement, run the real native app, inspect the result, compare it with the
   reference, identify remaining problems, and iterate. Do not substitute prose
   about intended results for implementation or screenshots.
4. Preserve the race track scene and its surroundings while improving the town.
   Fix the cause of defects without reintroducing previous rejected designs.
5. Do not declare reference parity merely because a checklist passes. Explicitly
   identify remaining gaps; a limitation statement does not make unfinished
   authorized work complete. If a real blocker prevents progress, explain it.
6. Own missed regressions. Explain what the earlier test did not check, add an
   appropriate check, and demonstrate the fix. Do not repeatedly leave the user
   to discover obvious visual or behavioral errors.
7. Commit and push along the way, with Entire provenance, as specified below.

## 2. Reference research and comparison

- Use **Mos Eisley and Mos Espa** as the architectural and settlement references
  for original **Mos Aster**. Match their style rather than copying a layout
  exactly. Include Star Wars film references for weather as well as architecture.
- Find both **true aerial/top-down views** and **street/player-height views**.
  A low oblique image is not a top-down map. When asked for aerial views or maps,
  provide that actual viewpoint, not a loosely related establishing shot.
- Compare building footprints, massing, roof forms, colors, weathering, attached
  outdoor areas, spacing, street setbacks, paths, courts, open spaces, props,
  street life, and transitions into desert. A collection of domed boxes is not
  sufficient resemblance.
- Use the user's supplied top-down settlement illustration as a layout reference:
  irregular connected compounds, varied roofs and domes, docking courts, narrow
  passages, wider public spaces, outdoor machinery, and mottled ground. It is an
  illustration, not a movie still; label references honestly.
- Keep sources and the observations derived from them in the relevant design
  document. Distinguish movie frames, television frames, game screenshots,
  concept art, and real set photography. Do not claim to have viewed a blocked
  or inaccessible reference.
- Compare native captures at useful matching viewpoints and scale. Show actual
  work when requested, including imperfect work; do not substitute references,
  generated concept art, or an unrendered plan for simulator screenshots.

Existing reference collections and comparisons: [city work](dirt-city.md),
[original plan](dirt-town-plan.md), [resident motion](town-resident-motion.md).
Historical claims of “final” in these documents are not user acceptance.

## 3. Town layout, roads, and ground

### Settlement structure

- Surround the race track with a substantial, detailed town. The normal zoomed
  out race view should not reveal empty desert outskirts. This does not prohibit
  desert in wider exploration views or beyond the settlement.
- Do not frame the town or track with a square perimeter, rectangular street
  circuit, square ground-color patch, or visibly artificial settlement boundary.
- Streets must connect meaningful destinations and fit the surrounding buildings.
  Remove arbitrary zig-zags, repetitive driveways, and roads drawn merely to fill
  space. Use plausible differences in street/path width, color, wear, and scale.
- Do not move houses away from the track just to accommodate a road redesign.
  Preserve the established race-facing density and quality.
- Dense compounds still need usable walking paths. Check the area south of the
  grandstands and every other district: residents must be able to reach doors
  from a street or open alley. Buildings must not be glued together across the
  only access route.
- Provide varied open spaces and non-building elements: markets, repair yards,
  courtyards, shade, machinery, storage, parked vehicles, and public gathering
  spaces. Avoid both empty outskirts and uniformly packed building grids.

### Architecture and everyday detail

- Vary building shapes, footprints, heights, roof forms, domes, extensions,
  materials, and colors. Add convincing imperfections: dirt, damaged plaster,
  chipped or broken areas, holes where appropriate, and maintained versus worn
  surfaces. Do not apply the same damage or dirt pattern to every house.
- Outdoor areas are part of the buildings: entrances, courtyards, shaded work
  areas, storage, tools, wares, seating, service equipment, and roof details.
- Make recognizable places with a purpose, including a droid-parts seller,
  bar/cantina, and market. Their architecture, props, activity, and signs should
  communicate their use.
- Vary doors in design and placement. Orient entrances toward reachable streets
  or open alleys, not uniformly toward one world direction or an obstruction.
- Labels and signs need deliberate placement, scale, alignment, styling, and
  mounting. Real cities and tracks have varied signage; a generic left-aligned
  banner is not enough.
- Buildings should retain appropriate close-up detail as Marvin explores farther
  from the circuit. Race-only distance optimizations must not strip nearby town
  buildings of their detail during exploration.

### Ground and desert transition

- Ground must not be one homogeneous color. Use organic, non-repeating variation
  informed by references: traffic wear, sheltered soil, deposited sand, edges,
  and local activity. Avoid regular procedural stamps and obvious tiling.
- Roads extending into dunes must progressively break up or become covered by
  windblown sand. Do not leave pristine, abrupt road strips running into desert.
- Blend the settlement into dunes naturally, without square edges, visible mesh
  seams, repetitive borders, or exposed undersides.
- After layout changes, drive track → town → dunes → town → track and review
  both aerial and player-height captures across all affected districts.

## 4. Track boundaries, entrances, and service area

### Track and masonry

- Use a track color that belongs in the desert city but remains distinct from
  ordinary yellow sand. The earlier dark track was rejected.
- Replace the **whole track side**, not only the old fence rail, with brick
  retaining walls approximately the requested fence height and an irregular top.
- Stack bricks horizontally from the bottom. On inclines, build stepped courses;
  do not tilt bricks diagonally to fake height changes. Use half bricks and
  convincing terminations, especially at the gate. Keep neighboring wall heights
  consistent and ensure spectators can see above them.
- Preserve the approved red-sand bleed into the infield and extend natural red
  deposits outside the wall, as if thrown over it by racing robots.

### Infield service area

- No houses in the track's middle. Use a robot repair area beneath tents, with
  spare parts, tools, and varied rusty salvage placed in plausible locations.
- Provide a real entry/exit in the boundary. Robots must be able to reach both
  tents, move past one another where intended, and return to the track.
- The orange tent and its floor must be triangular and fitted into the left,
  narrow corner away from the entrance. Earlier requests included rotating it
  180 degrees, then further clockwise and farther left. Preserve the final
  clearance intent; do not cumulatively replay historical rotations.
- Keep the approved patterned textile/carpet under the tents to keep sand out.
  Do not turn the whole service area into a tiled plaza or erase the approved
  carpet while correcting surrounding soil.
- Outside the carpet, use natural ground and red sand carried from track edges
  and entry/exit traffic. Avoid sterile, regular floor patterns.
- Service ramps must be gradual, fully supported earth with natural shoulders
  and mixed sand colors. No overly steep lip, triangular void, floating sheet,
  or camera view beneath the sand. Rendered terrain and collision support must
  agree.
- Tents, salvage, walls, ramps, people, and buildings require proper collision
  behavior. Decorative placement must not silently block required routes.

### City exit gate

- Provide a metal gate in the **outer wall of curve 1**, with a ramp into town.
  Hidden **G** opens it outward so Marvin can leave the course.
- Animate the gate and robot motion plausibly, with collision handling throughout
  the swing and passage. Do not solve clearance by disabling collisions.
- Raise/shape track height along the gate to eliminate the triangular gap beneath
  it, and adjust the outside ramp to match. Avoid low or malformed adjacent walls.
- Test leaving, entering, and roaming town, including all four robot bodies.
- Apply the same supported, continuous exit terrain and matching mesh treatment
  to the service-area entrance; inspect both sides and preserve all-chassis access.
- Service-ramp sand must blend into the infield with soft, irregular shoulders
  and continuous material mapping, never a visibly separated slab or apron patch.
- Both exits need a gradual track shoulder and continuous track-to-ramp material
  and shading. Review track, ramp, and surrounding ground together at matching
  aerial scale and driving height; fixing one join must not introduce another.
- Do not accept a remaining light band as a polish caveat. Maintain continuous
  texture scale across the lane, berm, shoulder, and both ramps; restarting a
  full texture on a narrow shoulder creates an artificial stripe.
- Compare both exits in actual aerial captures and have the independent judge
  assess the full transitions, including physical support and visible seams.

## 5. Driving, robots, sand, dust, and lighting

### Driving and character behavior

- Investigate steering/braking assistance using other racing games as references.
  Assistance should make driving more enjoyable and controllable, not fight
  player intent. Validate actual corners, braking, steering, and exits.
- Investigate unexplained yellow-sand slowdown rather than assuming it is desired
  difficulty. Do not reintroduce the previously reported town/sand speed defect.
- Heads and bodies must behave naturally during racing and exploration. Marvin
  must not stay looking right after leaving the track. BB-8's movement and stall
  issues require specific reproduction, not an unrelated generic test.
- Robot/terrain contact must work for every chassis, especially WALL-E crossing
  dunes diagonally. Partial sinking can be appropriate; exposed sand edges,
  clipping sheets, or visibly unreal contact are not. Research and implement
  convincing sand contact rather than removing the intended sinking effect.

### Surface-dependent effects

- Preserve persistent footprints/tread or rolling impressions across track,
  town, and dunes. Match each robot's contact geometry and the local ground.
- Derive impression appearance from the surface beneath it. Do not use one fixed
  color everywhere. Marks must remain visible over ground dressing and obey
  occlusion by terrain, robots, and buildings.
- Keep the clods the user liked on the dirt track. Make spray, clod appearance,
  and dust dynamic by surface: red/clay clods on the track; appropriate fine sand
  and dust in yellow sand. Do not remove all clods to fix sand spray.
- Robot dust must be visibly present when appropriate, not merely emitted by a
  counter or blended into invisibility. Check direction, movement, contact,
  lighting, opacity, color, settling, and wind response in screenshots/video.
- Dust and sand effects must not become white floating clouds or repeated blobs.

### Two suns and daylight

- The planet has two suns. Research plausible relative positions and compelling
  Star Wars-like compositions. Choose a random daylight time before each race,
  between sunrise and sunset.
- Lighting, glare, sky, and shadows must agree with sun positions. Sunrise and
  sunset should be visually compelling; the user rejected the initial bland
  versions. Simulate multiple races and show sunrise/sunset captures when asked.
- Fix camera-dependent robot shadow flicker, missing shadows, and glitches. Test
  the same stationary scene from multiple angles; a single attractive screenshot
  is not sufficient.

### Sandstorms

- Sandstorms occur randomly in **1 out of 10 races**, not via a player setting.
  Each new race and **Cmd-R** reset must participate correctly in the randomness.
  One dry streak alone neither proves nor disproves a 10% probability.
- In a storm race, show **“A storm is coming...”** before the start.
- Ground the look in real sandstorm photographs/video and Star Wars references.
  Use very few spectators and substantially fewer people outdoors.
- Wind affects driving and robot spray. Sand deposits/mini dunes on the track
  must evolve with the storm, not remain an unrelated static decoration. Check
  whether visible height changes agree with terrain support and driving; do not
  describe color overlays as physical terrain deformation.
- Test clear and storm races, repeated resets, and affected robot behaviors.

## 6. Residents and spectators

- Include spectators in stands embedded into the town. They should wave, turn
  their heads, and react; town residents should walk, enter houses, and show
  convincing activity visible during an actual drive-through.
- Do not leave a population of statues facing the same direction. Give people
  independent, contextually sensible orientations and behavior.
- A stationary person must have a purpose: conversation with another person or
  group, work such as selling goods, waiting at a door, attending a vehicle, or
  leaning against a wall. Avoid isolated, aimless figures standing in a street.
- Feet/shoes must remain attached to legs throughout motion and at every detail
  level. Check varied poses and camera distances, not just the rest pose.
- Residents must navigate reachable routes, avoid collisions, yield, recover,
  and resume useful activity. Reproduce and fix stuck residents, including
  sidestepping and returning near building corners or robot obstructions.
- Avoid wasting full animation work on residents outside the camera view. Keep
  their logical activity coherent and resume visible animation without freezing,
  teleporting, or exposing an incorrect pose when the camera returns.
- Storm population changes must also update activity and collisions coherently.

## 7. Race lifecycle, cameras, maps, and loading

### Finish and continued town life

- After the race ends and **all four robots have completed at least one extra
  lap**, open the gate and let them leave. Do not open it prematurely.
- Give robots different routes and natural departures. Avoid identical scripted
  exit motions, synchronized turns, rigid “train track” paths, and destinations
  where they simply stop forever.
- Continue exploration for **10+ minutes** in validation. From the fixed race
  overview, the viewer should occasionally see robots pass again as they explore.
- Once the robots begin leaving, hide **all HUD**. Hiding overlays must not resize
  the camera viewport or change its projection/framing.
- Post-race viewing stays locked in bird's-eye view over the track so the player
  sees all four leave. Do not switch to Marvin's perspective or follow him into
  town. Disable player camera changes once the race is over.

### Manual exploration is a separate state

- During player-controlled exploration, change the map context on boundary
  crossings: **COURSE → TOWN → DUNES → TOWN → COURSE**.
- Keep the same overlay width. Town maps must use the available area legibly,
  without excessive blank margins shrinking the town. The dunes map shows the
  player's position relative to town.
- Do not show race-position/rival markers on TOWN or DUNES maps. Preserve the
  course outline and navigation information appropriate to those maps.
- With **C** selecting bird's-eye mode, use the fixed course overview on track,
  then smoothly follow Marvin from above when outside it. Show surrounding town,
  dunes, or both as appropriate. Smoothly return to the course overview on reentry.
- Test actual input and a complete journey in both directions, not only a
  teleport to one destination. This exploration behavior does not override the
  locked post-race camera described above.

### Loading

- Entering Dirt Track from the main menu needs a loading screen with a progress
  bar because level preparation takes time. Progress must represent preparation,
  remain responsive, and complete before gameplay is revealed.

## 8. Audio

- Provide cohesive, proximity-aware driving and character sounds for all four
  robots and appropriate nearby crowd sound during racing.
- Research R2-D2, BB-8, and WALL-E using movie sound and sound-design references.
  The user explicitly chose **close original recreations**, not copied movie
  recordings. Revisit WALL-E too; earlier praise was not a permanent exemption.
- Use those three characters as inspiration for a better, distinct Marvin voice.
  Do not substitute a tiny generic oscillator palette and call it authentic.
- Driving sounds matter as much as voices and should suit each robot's mechanics.
  Compare actual playback with references, not only file metadata or waveforms.
- Avoid the same phrase repeating. Use meaningful expressive variation and have
  rivals address the player on overtakes, rather than endless periodic chatter.
- The crowd cheers after the finish, then goes quiet as spectators leave the
  stadium. Finishing the race must not prematurely silence the celebration.
- Audio validation must distinguish technical correctness from perceived quality.
  Do not claim to have listened to output or matched a movie voice without doing
  so. See [race audio](race-audio.md) for references and known audition limits.

### October 2 sound-design correction

- Make every droid's mechanical driving clearly louder and richer with speed.
  Boost needs distinct onset, sustained thrust and release, driven by actual input.
- Reduce excessive squeaking/voice density; mechanical feedback must lead the mix.
- Give Mos Aster localized city life and continuing exploration audio.
- Sandstorms need evolving wind and airborne-grit audio tied to gusts and shelter,
  with less exposed city activity and clear driving/boost feedback through the mix.
- Use an independent Codex Astra judge at Ultra reasoning effort for this redesign.
  Do not claim AAA perceptual quality from code inspection or signal metrics alone.

## 9. Apple rendering and frame pacing

- Show a live FPS readout during gameplay, including manual town exploration.
  Measure rendered frames rather than the simulation update rate.

- Investigate Apple's recommended 3D/game frameworks and rendering features and
  use suitable GPU acceleration. Do not assume a framework name proves that all
  workloads are accelerated or that a rewrite will improve the game.
- Preserve stable frame rates while improving realism, crowd behavior, shadows,
  weather, audio, and exploration detail. The user specifically objected to drops
  to **58 fps** and requested investigation of missed frames.
- Validate contextual detail: optimize distant race scenery while retaining
  convincing nearby detail when the player enters the town.
- Benchmark the visually improved result. Do not use early benchmarking as an
  excuse to stop art iteration or claim reference parity. Measure throughout
  where useful to catch regressions, without substituting timing for visual QA.
- State hardware, resolution, scene/weather, duration, and measurement method.
  Inspect frame-time distributions and long frames, not just average FPS.
  SceneKit callback timing is not GPU execution or display-presentation timing.
- Keep benchmark conditions controlled. Building, multiple simulators, emulators,
  or heavy inspection tools can confound measurements. The historical request to
  kill the Android emulator was for that session, not permission to terminate
  arbitrary applications in every future session.

See [renderer study](renderer-study.md) for actual experiments and limitations;
its measurements and framework choices are not immutable product requirements.

## 10. Required validation practice

Choose checks relevant to the change and its likely regressions. Documentation-only
changes need consistency/link review, not a full race run. For runtime changes,
use the real built simulator and exercise the affected user flow.

### Before editing

- Read this guide and the relevant current code/design notes; inspect working
  tree changes and the target branch.
- Reproduce the reported failure with the relevant robot, camera, ground,
  weather, race state, and location. Save a baseline when practical.
- Identify what observable result would fail before and pass after the fix.
  Preserve those reproduction conditions in a test or documented capture route.

### Visual and behavioral checks

| Changed area | Required evidence |
| --- | --- |
| Town layout/art | True top-down town view, district-by-district review, player-height drive-throughs, reference comparison, preserved race surroundings and door access. |
| Infield/walls/gates/ramps | Service-area overview and close low-angle shots; entrance and both tents accessible; supported terrain, proper brick courses, collision-tested passage both ways. |
| Trails/dust/ground layers | Final rendered images with and without effects; verify visible impressions on dressed town ground, track, and dunes; inspect airborne dust and surface-dependent clods. Generated counts alone do not pass. |
| Shadows/LOD | Same stationary robot from several camera angles and distances; moving-camera review for flicker/popping; every relevant robot and detail tier. |
| Residents | Visible motion in timed image pairs/video; purposeful orientations; attached feet; blocked-route recovery and sustained house visits; off-screen updates and camera return. |
| Post-race | Full race and every robot's extra lap; correct gate timing; all four depart visibly; no HUD or viewport change; camera inputs locked; 10+ minutes of continued roaming and occasional returns. |
| Exploration maps/camera | Actual C/G inputs and track → town → dunes → town → track; correct labels/markers; usable map scale; smooth following and reentry. |
| Dune contact | All four robots, diagonal slopes, reverse, pivoting, stops, low-angle views, and consistent terrain/contact rendering. |
| Daylight/weather | Multiple races, sunrise/sunset, clear/storm conditions, real Cmd-R resets, warning overlay, population changes, wind and evolving deposits. |
| Audio | Listening comparison and in-game proximity, overtakes, phrase variety, crowd finish/exit lifecycle; technical playback checks separately. |
| Loading/HUD | Real menu entry, responsive and meaningful progress; pause/reset behavior; HUD removal without viewport changes. |

- **Look at the saved images.** A successful screenshot write or absence of shader
  error pixels does not establish correct art, effects, map size, or geometry.
- Compare the affected state before/after. Add checks that observe the actual
  failure, not assertions that merely mirror the implementation or count objects.
- Exercise interactions: different robots, surfaces, angles, detail levels,
  clear/storm lighting, pause, restart, and transitions as applicable.
- Long simulations must inspect behavior over time: peak stuck duration,
  completed visits, continuing movement, and collisions. A good final frame can
  hide a resident stuck for minutes earlier in the run.
- Keep machine-readable reports and images/video together with the conditions
  used. Distinguish simulated duration from real-time performance duration.
- Separate visual quality, navigation/physics, audio perception, and performance
  acceptance. Passing one does not imply the others passed.

### Existing entry points

Build using `apps/simulator-macos/build-app.sh`. The native binary is
`apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator`.
Most native checks take an output directory immediately after their flag. Confirm
current options in the implementation before assuming an old invocation is valid.

| Purpose | Existing entry point / documentation |
| --- | --- |
| Core physics | `swift run --package-path apps/simulator-macos -c release SimulationChecks`; toolchain fallback is documented in the simulator README. |
| Navigation/map/town tread visibility | `--navigation-smoke-test` |
| Town layout and entrances | `--town-smoke-test`, `--entrance-smoke-test` |
| Infield and town departure | `--city-escape-smoke-test`, [city history](dirt-city.md) |
| Post-race roaming | `--postrace-smoke-test` |
| Resident movement/recovery | `--people-smoke-test`; `MARVIN_PEOPLE_OFFSCREEN=1` and `MARVIN_YIELD_RECOVERY_TEST=1` select specific scenarios. |
| Camera/shadow/viewport regressions | `--visual-regression-test`, `--viewport-smoke-test` |
| Surface effects | `--trail-material-smoke-test`, `--dust-visibility-test`, `--debris-smoke-test`, `--dune-contact-test` |
| Sky/storm/restarts | `--binary-sky-smoke-test`, `--sandstorm-smoke-test`, `--weather-reset-test`, `--storm-race-test` |
| Audio/loading | `--audio-smoke-test`, `--loading-smoke-test` |
| Controlled performance | `--town-benchmark`; relevant roaming modes are documented in the city and dune notes. |

These are starting points, not a claim that current tests cover every requirement.
Expand the relevant checks when a new failure reveals a gap.

## 11. Evidence delivery, commits, and Entire

- Show new screenshots when requested: actual aerial/bird's-eye, robot POV,
  service-area, and other specified views. Clearly identify the camera and state.
- When asked for a movie, provide actual playable/downloadable video of the
  simulation. If the user is on a phone or needs Safari, a Mac-local file path is
  not a usable download link. Use an authorized accessible sharing mechanism,
  verify the result, and do not invent a public URL or silently expose private
  project material through an unapproved service.
- Report what changed, what was actually tested/viewed/heard, what failed or
  remains unresolved, and where the artifacts are. Never claim an unrun test.
- Work on the intended feature branch; the user asked for a new branch initially
  and repeatedly requested commit/push during continued work. Confirm the current
  branch rather than blindly reusing a historical name or pushing to main.
- Attach the appropriate **Entire session and checkpoint trailer** to commits and
  verify both the commit and checkpoint reached the remote. A local success
  message or a trailer alone does not prove a remote session is available.
- Use the supported attachment flow for the current session. Do not fabricate a
  checkpoint ID, reuse another session's ID, or claim that reattaching an existing
  checkpoint proves the latest transcript was newly captured. Disclose that
  distinction where relevant.
- The user previously authorized hook approval and later requested an empty
  provenance commit when hooks were unavailable. Do not repeatedly tell the user
  to run `/hooks`: it was not valid in this desktop environment, and the user
  could not open a separate CLI. Check actual capabilities and permissions;
  report a real blocker honestly and use supported attachment alternatives.
- An empty provenance commit was a specific repair request, not a requirement
  for every change. Never claim hooks are approved without evidence.

## 12. Historical corrections to preserve

| Earlier/rejected behavior | Standing correction |
| --- | --- |
| “Ranks” around the track | The user meant spectator stands, including people. |
| Houses in the infield | Repair tents, tools, and spare parts with usable access. |
| Bare/sterile service ground, then an overcorrection | Natural carried sand outside; preserve patterned tent carpets. |
| Arbitrary zig-zags, rectangular street ring, repeated driveways | Purposeful irregular town circulation with pedestrian access. |
| Houses moved away from the race, or packed across every doorway | Preserve race density while keeping alleys and entrances usable. |
| Diagonally tilted bricks or fence-only replacement | Horizontal stepped masonry replacing the complete track side. |
| Steep hollow ramps and gaps under the city gate | Supported, gradual earth and matching track/ramp elevations. |
| One dirt/effect color everywhere | Ground-dependent impressions, clods, spray, and visible dust. |
| A manual storm setting | Independent 10% race chance, including Cmd-R, with pre-start warning. |
| A post-race chase camera and robots that stop after departure | Locked track overview, hidden HUD, natural sustained roaming. |
| A fixed course map/fixed overhead camera during manual exploration | Contextual COURSE/TOWN/DUNES maps and smoothly following overhead view. |
| Footsteps exist in geometry but vanish beneath town dressing | Verify visible final pixels above ground layers with depth occlusion. |
| Simulator/benchmark success used as visual approval | Inspect actual captures and compare with the requested references. |

Keep this guide current as the user refines requirements. Do not mark its open
quality targets as achieved merely by copying them into a handoff or checklist.
