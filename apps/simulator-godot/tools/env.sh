# Source this file: sets up .NET and Godot for the Marvin Godot port.
# DOTNET_ROOT: ~/.dotnet when that install exists (this Mac), otherwise whatever dotnet is on PATH.
# GODOT: the Godot 4.7.2 .NET editor binary; set it explicitly outside macOS (README.md, "Windows").
if [ -z "$DOTNET_ROOT" ] && [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; fi
# Git Bash on Windows: DOTNET_ROOT is a Windows path (C:\Program Files\dotnet), whose colon would split PATH.
dotnet_dir="$DOTNET_ROOT"
if [ -n "$dotnet_dir" ] && command -v cygpath > /dev/null 2>&1; then dotnet_dir="$(cygpath -u "$dotnet_dir")"; fi
export PATH="${dotnet_dir:+$dotnet_dir:}/opt/homebrew/bin:$PATH"
export GODOT="${GODOT:-$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot}"
export MARVIN_GODOT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
