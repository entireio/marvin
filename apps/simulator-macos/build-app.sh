#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
# Use the installed standalone CLT if Xcode is selected but unavailable (for
# example, awaiting a license agreement). Do not change the global selection.
if ! /usr/bin/xcrun --find swift >/dev/null 2>&1 && [ -x /Library/Developer/CommandLineTools/usr/bin/swift ]; then
  export DEVELOPER_DIR=/Library/Developer/CommandLineTools
fi
# Swift Build's Xcode-oriented backend adds framework/library search paths
# absent from standalone CLT and requests the legacy arclite support library.
# Use SwiftPM's native backend for CLT; retain the default with full Xcode.
# Keep this selection identical for the build and its output-path lookup.
set --
case "${DEVELOPER_DIR:-$(/usr/bin/xcode-select -p)}" in
  */CommandLineTools|*/CommandLineTools/)
    set -- --build-system native
    ;;
esac
configuration=${CONFIGURATION:-release}
python3 "$root/scripts/export-marvin-simulator.py"
python3 "$root/scripts/export-r2d2-simulator.py"
python3 "$root/scripts/export-racers-simulator.py"
swift build "$@" --package-path "$root/apps/simulator-macos" -c "$configuration" --product MarvinSimulator
bin=$(swift build "$@" --package-path "$root/apps/simulator-macos" -c "$configuration" --show-bin-path)
app="$root/apps/simulator-macos/.build/Marvin Simulator.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$root/apps/simulator-macos/Info.plist" "$app/Contents/Info.plist"
cp "$bin/MarvinSimulator" "$app/Contents/MacOS/MarvinSimulator.new"
mv -f "$app/Contents/MacOS/MarvinSimulator.new" "$app/Contents/MacOS/MarvinSimulator"
# Replace this generated bundle directory so retired model files cannot linger.
rm -rf "$app/Contents/Resources/R2D2" "$app/Contents/Resources/BB8" "$app/Contents/Resources/WallE"
cp -R "$root/apps/simulator-macos/Resources/R2D2" "$app/Contents/Resources/"
cp -R "$root/apps/simulator-macos/Resources/BB8" "$app/Contents/Resources/"
cp -R "$root/apps/simulator-macos/Resources/WallE" "$app/Contents/Resources/"
cp -R "$root/apps/simulator-macos/Resources/Dirt" "$app/Contents/Resources/"
cp -R "$root/apps/simulator-macos/Resources/Marvin" "$app/Contents/Resources/"
cp "$root/apps/simulator-macos/Resources/Icons/"*.icns "$app/Contents/Resources/"
cp "$root/LICENSE-hardware" "$app/Contents/Resources/LICENSE-hardware"
codesign --force --sign - "$app"
printf '\nBuilt: %s\n' "$app"
