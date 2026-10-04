#!/bin/sh
# Builds RobotCloseups (the SceneKit twin of `tools/godot -- --robot-closeups DIR`) into OUT (default
# /tmp/robot-closeups-ref) from the macOS game's own sources. Nothing under apps/simulator-macos is modified.
set -eu
here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
sources="$here/../../../../simulator-macos/Sources"
out=${1:-/tmp/robot-closeups-ref}
mkdir -p "$out"
swiftc -O -parse-as-library -emit-module -emit-library -static -module-name SimulationCore \
  -emit-module-path "$out/SimulationCore.swiftmodule" "$sources"/SimulationCore/*.swift -o "$out/libSimulationCore.a"
app="$sources/MarvinSimulator"
swiftc -O -I "$out" -L "$out" -lSimulationCore \
  "$app/Robot.swift" "$app/R2D2.swift" "$app/ImportedRacer.swift" "$app/TrackBelt.swift" \
  "$app/DirtCoating.swift" "$app/World.swift" "$app/FloorGroove.swift" \
  "$here/RobotCloseups.swift" -o "$out/RobotCloseups"
echo "Built $out/RobotCloseups"
