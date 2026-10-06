# Source this file: sets up .NET and Godot for the Marvin Godot port.
# DOTNET_ROOT: ~/.dotnet when that install exists (this Mac), otherwise whatever dotnet is on PATH.
# GODOT: the Godot 4.7.2 .NET editor binary; set it explicitly outside macOS (README.md, "Windows").
if [ -z "$DOTNET_ROOT" ] && [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; fi
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}/opt/homebrew/bin:$PATH"
# MARVIN_ENGINE=marvin: the patched engine built by engine/build-engine.sh (engine/README.md; export templates
# 4.7.2.stable.marvin.mono). Without it (or with MARVIN_ENGINE=stock) the stock Godot 4.7.2 .NET editor.
if [ -z "$GODOT" ] && [ "${MARVIN_ENGINE:-stock}" = marvin ]; then
    export GODOT="${MARVIN_ENGINE_APP:-$HOME/Applications/GodotMarvin/Godot_mono.app}/Contents/MacOS/Godot"
fi
export GODOT="${GODOT:-$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot}"
export MARVIN_GODOT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
