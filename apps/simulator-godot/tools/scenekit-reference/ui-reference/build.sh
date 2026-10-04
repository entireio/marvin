#!/bin/sh
# AppKit reference captures for the Godot UI port (scripts/UI): compiles the macOS game's own UI files
# (RaceHUD, RaceNavigationMap, SimulatorView/HUDView, FrameRateHUD) with a copy of SimulationCore and
# renders the synthetic states of `tools/godot -- --hud-smoke-test DIR` through AppKit (cacheDisplay).
#
#   apps/simulator-godot/tools/scenekit-reference/ui-reference/build.sh OUT_DIR
#
# The macOS sources are only read. The SimulationCore copy gets one addition (Synthetic.swift's
# DirtRace.synthetic, appended to the copied DirtRace.swift because the stored properties are private(set)),
# mirroring UISmoke.SyntheticRace; it is compiled with -enable-testing so DirtOpponent.race can be set.
set -e
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../../../.." && pwd)
mac="$repo/apps/simulator-macos/Sources"
out=${1:?usage: build.sh OUT_DIR}
work=$(mktemp -d "${TMPDIR:-/tmp}/ui-reference.XXXXXX")
mkdir -p "$work/core" "$out"
cp "$mac"/SimulationCore/*.swift "$work/core/"
cat "$here/Synthetic.swift" >> "$work/core/DirtRace.swift"
(cd "$work" && swiftc -O -enable-testing -parse-as-library -emit-library -emit-module -module-name SimulationCore -Xlinker -install_name -Xlinker @rpath/libSimulationCore.dylib core/*.swift -o libSimulationCore.dylib)
swiftc -O -I "$work" -L "$work" -lSimulationCore -Xlinker -rpath -Xlinker "$work" \
  "$mac/MarvinSimulator/RaceHUD.swift" "$mac/MarvinSimulator/RaceNavigationMap.swift" \
  "$mac/MarvinSimulator/SimulatorView.swift" "$mac/MarvinSimulator/FrameRateHUD.swift" \
  "$here/Stubs.swift" "$here/main.swift" -o "$work/ui-reference"
"$work/ui-reference" "$out"
