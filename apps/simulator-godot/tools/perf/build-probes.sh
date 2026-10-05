#!/bin/bash
# Build the DYLD_INSERT_LIBRARIES probes of tools/perf into DIR (default: $TMPDIR/marvin-perf):
#   window-inject.dylib  exact benchmark window frame and level (+ black backdrop) for the macOS game and Godot
#   metal-labels.dylib   names Metal command encoders by attachments/pipelines for Metal System Trace
#   main-stalls.dylib    logs main-thread run-loop stalls
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-${TMPDIR:-/tmp}/marvin-perf}
mkdir -p "$out"
clang -O2 -fobjc-arc -dynamiclib -arch arm64 -framework AppKit "$here/window-inject.m" -o "$out/window-inject.dylib"
clang -O2 -fobjc-arc -dynamiclib -arch arm64 -framework Metal -framework Foundation "$here/metal-labels.m" -o "$out/metal-labels.dylib"
clang -O2 -fobjc-arc -dynamiclib -arch arm64 -framework Foundation "$here/main-stalls.m" -o "$out/main-stalls.dylib"
for f in "$out"/*.dylib; do codesign -s - -f "$f" >/dev/null 2>&1 || true; done
echo "$out"
