#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
# Use the installed standalone CLT if Xcode is selected but unavailable (for
# example, awaiting a license agreement). Do not change the global selection.
if ! /usr/bin/xcrun --find swift >/dev/null 2>&1 && [ -x /Library/Developer/CommandLineTools/usr/bin/swift ]; then
  export DEVELOPER_DIR=/Library/Developer/CommandLineTools
fi
configuration=${CONFIGURATION:-release}
python3 "$root/scripts/export-marvin-simulator.py"
swift build --package-path "$root/apps/simulator-macos" -c "$configuration" --product MarvinSimulator
bin=$(swift build --package-path "$root/apps/simulator-macos" -c "$configuration" --show-bin-path)
app="$root/apps/simulator-macos/.build/Marvin Simulator.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$root/apps/simulator-macos/Info.plist" "$app/Contents/Info.plist"
cp "$bin/MarvinSimulator" "$app/Contents/MacOS/MarvinSimulator.new"
mv -f "$app/Contents/MacOS/MarvinSimulator.new" "$app/Contents/MacOS/MarvinSimulator"
cp -R "$root/apps/simulator-macos/Resources/Dirt" "$app/Contents/Resources/"
cp -R "$root/apps/simulator-macos/Resources/Marvin" "$app/Contents/Resources/"
cp "$root/apps/simulator-macos/Resources/Icons/"*.icns "$app/Contents/Resources/"
cp "$root/LICENSE-hardware" "$app/Contents/Resources/LICENSE-hardware"
codesign --force --sign - "$app"
printf '\nBuilt: %s\n' "$app"
