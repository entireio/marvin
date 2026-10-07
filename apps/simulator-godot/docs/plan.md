# Godot port: plan and status

Status as of 2026-10-07, branch `codex/mos-aster-racing-city`.

## Goal

Port the macOS game (`apps/simulator-macos`, Swift and SceneKit) to Godot 4.7 (.NET/C#) in `apps/simulator-godot`, so it runs on Windows, with the same features and the same graphics. The macOS game is the reference and stays untouched.

## Approach

- **Game logic:** `src/Core` is a file-by-file C# port of SimulationCore, with Swift-exact maths and random numbers. `src/Checks` ports the 39 SimulationChecks.
- **App code:** a SceneKit/AppKit/AVFoundation facade over Godot (`scripts/SceneKit`) lets the Swift app code translate almost line for line. The facade is calibrated against real SceneKit renders.
- **Verification:** each area is compared with the macOS game's own capture and smoke modes (same flags, cameras, file names and JSON keys), plus SceneKit reference tools in `tools/scenekit-reference`.

Details: `PORTING.md` (conventions, facade, comparison), `docs/performance.md` (measurements), `README.md` (building and running).

## Done

| Area | State |
|---|---|
| Game logic | `tools/checks` output is byte-identical to the Swift reference (debug build) on macOS. |
| All gameplay | Menu with 3D portraits, sandbox, 3-lap Dirt Track race with 4 robots, AI, assists, collisions, sandstorm, twin-sun daylight, Mos Aster town, crowd and residents, post-race escape, maps, HUD, pause, scores, settings, race audio and voice lines. Playable with keyboard and mouse. |
| Generated town | Identical to macOS: 497 buildings, 492/492 accessible compounds, 282 people, 4,976 collision bodies, same triangle counts. |
| Test modes | 51 game modes mirror the macOS flags. All pass except `--town-departure-movie`, which fails on macOS too. Deterministic reports are byte-identical to macOS (smoke, town, entrance, people, audio and others). |
| Look | Mean difference per area from the macOS captures: 0.5-3.4/255 (`PORTING.md`, "Full-game comparison"). |
| Settings (Godot-only) | **Shadow quality**: Fast (hard shadows, the default) or Exact (soft, matching macOS). **Graphics detail**: Max (the default, unchanged), High, Medium, Low. |
| Release builds | `tools/export` builds a macOS app and a Windows folder. The exported macOS app passes the smoke modes and the full playthrough. |
| Windows CI | `.github/workflows/godot-windows.yml` builds, tests, exports and renders the port on a GPU-less Windows runner on every push (see "Left to do", item 1). |
| Mac game changes | The port matches the macOS gameplay at `1c8bb11`. The only later macOS commits (`de62266`, `057f52e`) add diagnostics, not gameplay. |

### Performance at 1920 x 1080 (Apple M2, FPS)

| Graphics detail | Fast, town | Fast, race | Exact, town | Exact, race |
|---|---|---|---|---|
| Max | 54.9 | 57.9 | 39.8 | 47.4 |
| High | 59.7 | 59.9 | 45.2 | 55.0 |
| Medium | 59.9 | 59.9 | 59.8 | 59.9 |
| Low | 59.9 | 59.9 | 59.9 | 59.9 |
| macOS game | 60 | 60 | 60 | 60 |

At 960 x 540 every combination holds 60 FPS. The exported build measures the same as the editor runtime.

## Left to do

1. **Run it on Windows.** No Windows PC has run it yet, but CI now does on a GPU-less GitHub Windows runner
   (`.github/workflows/godot-windows.yml`, every push to the port; `PORTING.md`, "Release builds and Windows"): the
   simulation checks, the assets generated on Windows, the town, 12 logic modes headless, the Windows export and
   Direct3D 12 and Vulkan rendering in software. What it found:
   - **Everything runs.** All checks pass, all modes exit as on macOS, the export builds and runs, Direct3D 12
     renders through Microsoft's WARP adapter and Vulkan through Mesa lavapipe.
   - **The town is the same town.** `--town-statistics` is identical to macOS in all 27 keys. The full layout dump
     differs only in last digits (50 of 9,690 lines) and in up to 7 welded vertices in 15 of 417 cells.
   - **Fixed:** Float `normalize` used a different reciprocal square root off arm64, so normals and welded meshes
     differed (the street mesh had 13,367 vertices instead of 19,238). `Simd.rsqrt`/`recip` now reproduce the
     NEON instructions in software, bit for bit.
   - **Last-digit maths remains.** Windows' C runtime rounds `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`,
     `exp`, `log` and `pow` differently from macOS in 0.02-39 % of calls (by one ulp; Linux as well). Short runs
     stay identical (smoke, storm races, town, entrance, people, characters); long chaotic ones drift (5 of 36
     SimulationChecks lines, the three-lap race report, audio levels at 1e-10). Making them identical would need
     Darwin's functions reproduced (not public) or a correctly rounded maths library on every platform, macOS
     included, which would change the macOS reference. Decide whether to accept the drift.
   - **The exported Windows build crashes when it quits** after building the town (0xC0000374, heap corruption;
     its reports are complete). The debug template and the editor runtime quit cleanly. Under `cdb` it is an access
     violation in Godot's own shutdown code (no .NET frames); the official templates have no symbols, so the next
     step is a template built with symbols, or freeing the town's nodes before quitting to see if that avoids it.
   - Text: Windows draws the HUD in Segoe UI and Consolas and the signs in Bahnschrift; the layouts hold (HUD
     2.8/255 from macOS, the signs' text a little larger).

   Still to check on a real Windows PC: Direct3D 12 and the Vulkan fallback on a real GPU and FSR 2 at Medium and
   Low, performance on representative GPUs, audio output (WASAPI), the title bar, display scaling, keyboard layouts
   and Ctrl shortcuts.
2. **Max is still short of 60 FPS at 1080p** (Fast town 54.9, race 57.9; Exact 39.8 / 47.4). Every remaining saving found either changes the look (those are now in High, Medium and Low) or needs changes inside Godot itself.
3. **Known visual differences** (all in `PORTING.md`):
   - Fast shadows are hard-edged, with small stair-steps in close views. Exact is soft but lacks SceneKit's blotchy penumbra pattern.
   - SceneKit's deferred-shadow darkening in the sandbox and menu (an MSAA artefact) is not reproduced.
   - Shaded town walls are slightly brighter (SceneKit's SSAO for shader-modified materials).
   - Medium and Low are softer than Max: distant sign text becomes hard to read and robot outlines look jagged. The cause of the jagged edges has not been investigated.
4. **Smaller open items:**
   - Changing Graphics detail on the Settings screen freezes the menu for 0.1-0.3 s; the change could be deferred to the next race or sandbox start instead.
   - Godot occasionally hangs when quitting after a test mode, and the Metal driver logged one fence timeout on a first switch to Medium.
   - `--town-departure-movie` takes about 4 minutes in Godot against 30 s on macOS.
   - The repository's `check-sustained-performance.py` requires Metal, so it rejects benchmark runs from Windows.
5. **Keeping up with the Mac game.** New macOS gameplay changes have to be ported by hand, file by file, and checked with the same capture and smoke modes.

## Parked, needs a decision

- **Custom Godot build:** not authorised. Draft engine patches (cheap SceneKit-style soft shadows, fixed shadow boxes) were written and reviewed but never compiled. They are on the backup branch `godot-port/wf_5eea2468-bf1-1` and in a local worktree. The Godot 4.7.2 source is checked out at `~/src/godot-4.7.2-marvin`. Keep or delete.
- **Backup branches:** the `godot-port/*` branches on origin hold the agents' worktree commits. Everything in them has been replayed onto `codex/mos-aster-racing-city`, apart from the parked engine drafts. They can be deleted.
