# Marvin Simulator: Godot port

Agent contributors: read [PORTING.md](PORTING.md) (the translation rules, the SceneKit facade and how the port is
compared with the macOS game) and the [simulator rules](../../docs/simulator-agent-rules.md) first.

A Godot 4.7 (.NET, C#) port of the macOS game in [`apps/simulator-macos`](../simulator-macos), so that it runs on
Windows as well as macOS. The macOS game is the reference: the port mirrors its Swift code file by file over a
SceneKit/AppKit facade and is calibrated against its captures (PORTING.md). It is never modified from here.
Status and what is left: [docs/plan.md](docs/plan.md).

## Setup

On a Mac or a Windows PC with git and [mise](https://mise.jdx.dev/getting-started.html) (on Windows in PowerShell):

```sh
git clone --branch codex/mos-aster-racing-city https://github.com/entireio/marvin.git
cd marvin/apps/simulator-godot
mise trust
mise run setup
mise run play
```

`mise run setup` installs the tools `mise.toml` pins into mise's own folders, nothing system-wide (Godot 4.7.2 .NET,
the .NET 8 SDK, Python 3.12, Git LFS), fetches the Git LFS files the game is made from, generates the robots' meshes
from their sources (`scripts/prepare-simulator-assets.py`, as the macOS build does: no macOS build needed), copies the
assets into `assets/` (gitignored), builds the C# and imports the textures and audio. The first run takes a few
minutes, mostly downloads; after a `git pull` run it again (seconds when nothing changed). `mise run play` builds the C#
and starts the game.

| Task | |
|---|---|
| `mise run play` | The game: main menu, Sandbox, Dirt Track. Flags go to the game, e.g. `mise run play -- --smoke-test /tmp/smoke` (game modes: PORTING.md, "App and game modes"); Godot's own flags go before a second `--`. On a Mac it runs Godot from `build/dev/Marvin Simulator.app`, a copy of Godot's app (an APFS clone, signed ad hoc) named after the game, so the menu bar says Marvin Simulator instead of Godot; made on first play and again when Godot changes. |
| `mise run test` | A quick headless smoke test, the town and the four robots (about 20 s on a Mac), against the same run on Godot on macOS; `mise run test -- all` runs every mode the Windows CI runs. |
| `mise run checks` | The simulation checks (no Godot). |
| `mise run export` | Release builds (below). |
| `mise run build`, `mise run godot -- ARGS` | The C# only; Godot on this project with any arguments. |

The tasks are `tools/dev.py`, in Python so that they run the same on macOS and Windows. **Without mise**, install
Godot 4.7.2 .NET (on a Mac as `~/Applications/Godot_mono.app`, otherwise set `GODOT` to the editor binary, on Windows
the `_console.exe`), the .NET 8 SDK (`~/.dotnet`, or set `DOTNET_ROOT`), Python 3 and Git LFS, and run the tasks as
`python tools/dev.py setup`, `play` and so on. The bash scripts `tools/godot`, `tools/build`, `tools/checks` and
`tools/export` (Git Bash on Windows) find Godot and .NET the same way (`tools/env.sh`: `GODOT` and `DOTNET_ROOT`, then
mise's, then `~/Applications` and `~/.dotnet`). `MARVIN_MAC_RESOURCES` makes setup copy the assets from another
`Resources` folder of the macOS game that has the generated meshes, instead of generating them.

The editor runs the game from the project folder; release builds are exported (below). Keys: W A S D or the
arrows drive, Shift boosts, Space brakes, C changes the camera, P or Esc pauses, ? shows the key guide; ⌘M / ⌘R /
⌘P / ⌘1 (Ctrl on Windows) are the Simulation menu's Main Menu, Reset, Pause and Camera.

The port trades two small differences from the macOS game for frame rate at high resolutions: shadows have hard
edges, and the robots are drawn from simplified meshes where that changes less than half a pixel. Choose
Settings > Shadow quality: Exact in the game for the exact macOS look at a higher GPU cost (Fast is the default; the
choice is stored with the other settings and applies immediately; the macOS game has no such setting). For tests and
experiments the environment variable `MARVIN_SCN_CAL=Exact` selects it for any game mode, in the editor runtime and
the release builds alike, and overrides the setting (PORTING.md, "Known deviations"; `docs/performance.md`).

Settings > Graphics detail trades look for frame rate on GPUs that cannot run the full look at 60 FPS: **Max** (the
default) draws everything as before; **High** leaves out the ambient occlusion (SSAO) and simplifies four small details at
full resolution; **Medium** also draws the 3D view at 0.6 of the window's resolution and upscales it with MetalFX temporal
upscaling on macOS (FSR 2 on Windows), and lights the ground's overlay layers more simply; **Low** draws it at half the
resolution and simplifies further. The HUD and menus stay at full resolution. At 1920 x 1080 on an M2, High holds 60 FPS
with Shadow quality Fast, Medium with Exact, and Low leaves a large margin (`docs/performance.md`, "Graphics detail").
The choice is stored with the other settings and applies immediately; the two settings are independent; the macOS game
has no such setting. `MARVIN_GRAPHICS_DETAIL=Low|Medium|High|Max` selects a level for any game mode (game modes otherwise
use Max).

## Release builds

```sh
mise run export                       # on a Mac both: build/macos/Marvin Simulator.app, build/windows/MarvinSimulator.exe
mise run export -- macos              # or one platform (on Windows the default is windows)
mise run export -- windows --zip      # also build/MarvinSimulator-windows-x86_64.zip, to copy to a PC
```

(`tools/export` with the same arguments, without mise.) The first export fetches the files of Godot's export templates
that the presets need (`macos.zip` and the Windows x86_64 templates, about 200 MB of the 1.2 GB
`Godot_v4.7.2-stable_mono_export_templates.tpz`) into Godot's templates folder (macOS
`~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`, Windows
`%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`). The export imports, exports with Godot's release template and
then runs `tools/export-verify.py` on the result,
which checks the bundle without running it (so the Windows build is checked on a Mac too): the executables'
architectures and resources, the self-contained .NET runtime and the game assemblies (built as `ExportRelease`, no
editor assemblies), the native libraries the assemblies name (only the two macOS ones, both behind an OS check), and
the pack: every raw file the game reads with `FileAccess` (`geometry.bin`, the JSON meshes and manifests, all 161
`.wav` files) byte-identical to `assets/`, every imported texture, and nothing from `reference/`, `tools/`, `docs/`,
`captures/` or `src/`.

The presets are in `export_presets.cfg`:

| | macOS | Windows |
|---|---|---|
| output | `build/macos/Marvin Simulator.app` (universal: arm64 and x86_64; .NET data folder per architecture) | `build/windows/`: `MarvinSimulator.exe`, `MarvinSimulator.console.exe` (console wrapper for terminal runs), `MarvinSimulator.pck`, `data_MarvinGodot_windows_x86_64/` (the .NET 8 win-x64 runtime and the game assemblies). Keep the four together. |
| renderer | Metal (Forward+) | Direct3D 12 (Forward+), falling back to Vulkan when D3D12 cannot start (`project.godot`: `rendering_device/driver.windows`, `fallback_to_vulkan`) |
| signing | ad-hoc, with the entitlements .NET needs (JIT, unsigned executable memory) and the ones the performance probes use (DYLD environment, library validation off); not notarised | unsigned; icon and version resources set |
| raw assets | `include_filter`: `assets/*.json, assets/*.bin`; the `.wav` files are imported with the "keep" importer, so they are packed as they are | same |

The C# is published by Godot's export (`dotnet publish`, configuration `ExportRelease`); `MarvinGodot.csproj`
optimises every configuration, so the editor runtime and the export run the same code at the same speed
(`docs/performance.md`). An exported game takes its game-mode flags with or without `--`.

### Running the macOS build

Open `build/macos/Marvin Simulator.app`, or start game modes from a terminal:

```sh
"build/macos/Marvin Simulator.app/Contents/MacOS/Marvin Simulator" --smoke-test /tmp/smoke
```

Settings, scores and logs live in `~/Library/Application Support/Godot/app_userdata/Marvin Simulator/`
(`UserDefaults.cfg`, `motocross-v4-scores.json`, `logs/godot.log`), shared with the editor runtime.

## Windows

No Windows PC has run the game yet. CI does, on a GitHub-hosted Windows runner without a GPU
(`.github/workflows/godot-windows.yml`, on every push to the port): the simulation checks, the assets generated on
Windows, the town and 12 logic modes headless, `tools/export windows` and the exported exe, and Direct3D 12 (Microsoft's
WARP software adapter) and Vulkan (Mesa lavapipe) captures. Each run's summary page lists what differs from macOS, and
the Windows build is attached to the run (`MarvinSimulator-windows-x86_64`). Before that, everything that can be
checked on a Mac was: the export and its layout (above), the Windows user-interface paths (in-window menu bar, Control
shortcuts, a window fitted to a scaled screen), Godot's Vulkan renderer (through MoltenVK), the portable maths and the
thread stacks. Results: PORTING.md, "Release builds and Windows".

### Getting a build

**On Windows**: the commands in "Setup" (PowerShell, with Git for Windows and mise), then `mise run play`, or
`mise run export` for the release build in `build/windows`. CI runs exactly these on fresh Windows, macOS and Linux
runners (`.github/workflows/godot-setup.yml`).

**From a Mac**: `mise run export -- windows --zip`, copy `build/MarvinSimulator-windows-x86_64.zip` to the PC and unzip
it anywhere. No installation is needed; the .NET runtime is inside.

### Running

- Double-click `MarvinSimulator.exe`. The window has the system title bar; the game's menu bar (Marvin Simulator,
  Simulation) sits in the bar below it, and the shortcuts use Ctrl: Ctrl+M main menu, Ctrl+R reset, Ctrl+P pause,
  Ctrl+1 camera, Ctrl+Q quit. On a scaled display the window keeps the game's 1280 x 820 points at the display's scale,
  made smaller (down to 900 x 640) when that does not fit the screen, as macOS does: at 125 % or 150 % on a 1080p
  display the game lays out to the shorter window.
- From a terminal use the console wrapper, which prints the log and returns the exit code:

  ```bat
  MarvinSimulator.console.exe --smoke-test out\smoke
  MarvinSimulator.console.exe --town-smoke-test out\town
  MarvinSimulator.console.exe --audio-smoke-test out\audio
  ```

  (`--` before the flags works too, but Windows PowerShell drops a bare `--`.)
- Renderer: Direct3D 12 by default; if it cannot start, Godot falls back to Vulkan. Force one with
  `--rendering-driver d3d12` or `--rendering-driver vulkan`. The Direct3D 12 Agility SDK is not shipped, so D3D12 uses
  the runtime that comes with Windows 10/11. The first start compiles shaders and is slower; later starts use Godot's
  shader cache.
- Settings, scores, logs and the shader cache: `%APPDATA%\Godot\app_userdata\Marvin Simulator\`.

### What differs from macOS

- **Fonts.** SF Pro, SF Mono and Avenir Next Condensed are Apple's and are not shipped; Windows draws the HUD in
  Segoe UI and Consolas and the town signs in Bahnschrift. Text is measured and laid out as on the Mac (the key guide
  grows with its text, the signs shrink theirs to fit), but it looks different and has not been checked on Windows.
- **Window chrome.** The system title bar above the client area instead of the Mac's unified title bar; the menu bar
  is in the window (Windows has no global menu bar).
- **Last-digit maths.** `hypot` and the reciprocal square root behind `normalize` run managed copies of Darwin's and
  ARM's algorithms that return the same bits as macOS (`tools/checks --portable-math`). The other C library functions
  (`sin`, `cos`, `tan`, `atan2`, `exp`, `log`, `pow` ...) come from the Windows C runtime and differ from Darwin's in
  the last bit in up to a third of calls (measured in CI), so long chaotic simulations (a three-lap race with
  contacts) drift in their last digits from a macOS run, as the macOS release and debug builds do from each other.
  The generated town has the same statistics; short smoke reports (race smoke, storm races, town, entrance, people)
  are identical in CI, longer ones differ in some numbers.
- **Untested there:** a real GPU (CI renders with Direct3D 12 on WARP and with lavapipe), Windows audio output
  (WASAPI) and real display scaling.

## Layout

| Path | Contents |
|---|---|
| `project.godot`, `MarvinGodot.csproj`, `MarvinGodot.sln` | The Godot project (Forward+, C#; assembly `MarvinGodot`) |
| `export_presets.cfg` | Release presets: macOS (universal), Windows Desktop (x86_64) |
| `mise.toml` | The developer setup: the pinned tools and the tasks (`tools/dev.py`) |
| `src/Core`, `src/Checks` | The simulation core (pure C#) and the port of the macOS simulation checks |
| `scripts/` | The game, mirroring the Swift sources; `scripts/SceneKit/` is the SceneKit/AppKit facade |
| `tools/` | `dev.py` (the mise tasks), `env.sh`, `godot`, `build`, `checks`, `sync-assets.py`, `export`, `export-verify.py`; `perf/` (benchmarks, `docs/performance.md`); `ci/` (the Windows CI's scripts and references) |
| `assets/`, `build/`, `reference/` | Gitignored: synced assets, exports, macOS reference captures |
