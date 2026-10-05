# Marvin Simulator: Godot port

Agent contributors: read [PORTING.md](PORTING.md) (the translation rules, the SceneKit facade and how the port is
compared with the macOS game) and the [simulator rules](../../docs/simulator-agent-rules.md) first.

A Godot 4.7 (.NET, C#) port of the macOS game in [`apps/simulator-macos`](../simulator-macos), so that it runs on
Windows as well as macOS. The macOS game is the reference: the port mirrors its Swift code file by file over a
SceneKit/AppKit facade and is calibrated against its captures (PORTING.md). It is never modified from here.

## Requirements

- [Godot 4.7.2 .NET](https://godotengine.org/download/archive/4.7.2-stable/) (the "mono" editor) and, for release
  builds, its export templates (`Godot_v4.7.2-stable_mono_export_templates.tpz`, installed with
  Editor > Manage Export Templates, or unpacked into the templates folder: macOS
  `~/Library/Application Support/Godot/export_templates/4.7.2.stable.mono/`, Windows
  `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`).
- The .NET 8 SDK.
- Python 3 (asset sync, checks, export verification).
- The game's assets: `tools/sync-assets.py` copies them from `apps/simulator-macos/Resources` of the main checkout
  into `assets/` (gitignored). Marvin's `geometry.bin` and the BB-8 / WALL-E meshes are generated there by
  `apps/simulator-macos/build-app.sh`, so a Mac has to build the macOS game once (or set `MARVIN_MAC_RESOURCES` to a
  copy of a Resources folder that has them).

## Build and run (development)

From the repository root, on macOS (`tools/env.sh` expects the editor in `~/Applications/Godot_mono.app` and .NET in
`~/.dotnet`; set `GODOT` and `DOTNET_ROOT` otherwise):

```sh
apps/simulator-godot/tools/sync-assets.py               # once, and after the macOS assets change
apps/simulator-godot/tools/build                        # compile the C# (MarvinGodot + MarvinCore)
apps/simulator-godot/tools/godot --headless --import    # first time: import textures and audio
apps/simulator-godot/tools/godot                        # play: main menu, Sandbox, Dirt Track
apps/simulator-godot/tools/godot -- --smoke-test DIR    # a game mode: flags after "--" (PORTING.md, "App and game modes")
apps/simulator-godot/tools/checks                       # the simulation checks (no Godot)
```

The editor runs the game from the project folder; release builds are exported (below). Keys: W A S D or the
arrows drive, Shift boosts, Space brakes, C changes the camera, P or Esc pauses, ? shows the key guide; ⌘M / ⌘R /
⌘P / ⌘1 (Ctrl on Windows) are the Simulation menu's Main Menu, Reset, Pause and Camera.

The port trades two small differences from the macOS game for frame rate at high resolutions: shadows have hard
edges, and the robots are drawn from simplified meshes where that changes less than half a pixel. Set
`MARVIN_SCN_CAL=Exact` (an environment variable, for the editor runtime and the release builds alike) for the exact
macOS look at a higher GPU cost (PORTING.md, "Known deviations"; `docs/performance.md`).

## Release builds

```sh
apps/simulator-godot/tools/export                 # both: build/macos/Marvin Simulator.app, build/windows/MarvinSimulator.exe
apps/simulator-godot/tools/export macos           # or one platform
apps/simulator-godot/tools/export windows --zip   # also build/MarvinSimulator-windows-x86_64.zip, to copy to a PC
```

`tools/export` imports, exports with Godot's release template and then runs `tools/export-verify.py` on the result,
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

The Windows build has not been run on Windows: no Windows machine was available. Everything that can be checked on a
Mac was (PORTING.md, "Release builds and Windows"): the export and its layout (above), the Windows user-interface
paths (in-window menu bar, Control shortcuts, a window fitted to a scaled screen), Godot's Vulkan renderer (through
MoltenVK), the portable maths and the thread stacks.

### Getting a build

**From a Mac** (simplest): `apps/simulator-godot/tools/export windows --zip`, copy
`build/MarvinSimulator-windows-x86_64.zip` to the PC and unzip it anywhere. No installation is needed; the .NET
runtime is inside.

**On Windows**:

1. Install Godot 4.7.2 .NET for Windows (`Godot_v4.7.2-stable_mono_win64.zip`) and its export templates, the .NET 8
   SDK, Python 3 and Git for Windows (Git Bash runs the `tools/` scripts).
2. Clone the repository (Git LFS for the crowd mesh) and copy `apps/simulator-godot/assets/` from a Mac that ran
   `tools/sync-assets.py`, or run it with `MARVIN_MAC_RESOURCES` pointing at a copy of the macOS game's
   `Resources` folder that includes the generated meshes.
3. In Git Bash:

   ```sh
   export GODOT="/c/Godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
   cd apps/simulator-godot
   tools/build && tools/godot --headless --import
   tools/godot                      # play from the editor runtime
   tools/export windows             # release build in build/windows
   ```

   Without Git Bash, the same steps by hand: `dotnet build MarvinGodot.csproj`, then
   `Godot_v4.7.2-stable_mono_win64_console.exe --path . --headless --import` and
   `... --path . --headless --export-release "Windows Desktop" build/windows/MarvinSimulator.exe`.

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
- **Last-digit maths.** `hypot` runs a managed copy of Darwin's algorithm that returns the same bits as macOS
  (`tools/checks --portable-math`). The other C library functions (`sin`, `cos`, `atan2`, `exp`, `log`, `pow`) come from
  the Windows C runtime and can differ from Darwin's in the last bit, so long chaotic simulations (a three-lap race
  with contacts) can drift in their last digits from a macOS run, as the macOS release and debug builds do from each
  other. Smoke reports that are byte-identical between the macOS game and the port on a Mac may therefore differ in
  some numbers on Windows (the macOS release and debug builds drift like this and both pass every check).
- **Untested there:** the Direct3D 12 renderer, Windows audio output (WASAPI) and real display scaling.

## Layout

| Path | Contents |
|---|---|
| `project.godot`, `MarvinGodot.csproj`, `MarvinGodot.sln` | The Godot project (Forward+, C#; assembly `MarvinGodot`) |
| `export_presets.cfg` | Release presets: macOS (universal), Windows Desktop (x86_64) |
| `src/Core`, `src/Checks` | The simulation core (pure C#) and the port of the macOS simulation checks |
| `scripts/` | The game, mirroring the Swift sources; `scripts/SceneKit/` is the SceneKit/AppKit facade |
| `tools/` | `env.sh`, `godot`, `build`, `checks`, `sync-assets.py`, `export`, `export-verify.py`; `perf/` (benchmarks, `docs/performance.md`) |
| `assets/`, `build/`, `reference/` | Gitignored: synced assets, exports, macOS reference captures |
