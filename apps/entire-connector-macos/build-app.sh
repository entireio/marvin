#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
configuration=${CONFIGURATION:-release}
swift build --package-path "$root/apps/entire-connector-macos" -c "$configuration"
(cd "$root/apps/entire-connector" && go build -trimpath -o "$root/apps/entire-connector-macos/.build/marvin-entire-connector" .)
app="$root/apps/entire-connector-macos/.build/Marvin Entire Connector.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$root/apps/entire-connector-macos/Info.plist" "$app/Contents/Info.plist"
cp "$root/apps/entire-connector-macos/.build/$configuration/MarvinEntireConnector" "$app/Contents/MacOS/MarvinEntireConnector.new"
mv -f "$app/Contents/MacOS/MarvinEntireConnector.new" "$app/Contents/MacOS/MarvinEntireConnector"
cp "$root/apps/entire-connector-macos/.build/marvin-entire-connector" "$app/Contents/Resources/marvin-entire-connector.new"
mv -f "$app/Contents/Resources/marvin-entire-connector.new" "$app/Contents/Resources/marvin-entire-connector"
cp "$root/apps/entire-connector-macos/Resources/entire-symbol-dark-icon.svg" "$app/Contents/Resources/entire-symbol-dark-icon.svg"
echo "$app"
