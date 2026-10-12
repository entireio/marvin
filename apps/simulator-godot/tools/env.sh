# Source this file: sets up .NET and Godot for the Marvin Godot port (tools/godot, build, checks, export).
# Each comes from, in this order: GODOT (the Godot 4.7.2 .NET editor binary) and DOTNET_ROOT when set; the versions
# mise.toml pins, once mise installed them (mise run setup, README.md); ~/Applications/Godot_mono.app and ~/.dotnet;
# for .NET otherwise whatever dotnet is on PATH. tools/dev.py looks them up the same way.
export MARVIN_GODOT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
marvin_mise="$(command -v mise 2> /dev/null || true)"
if [ -z "$marvin_mise" ] && [ -x /opt/homebrew/bin/mise ]; then marvin_mise=/opt/homebrew/bin/mise; fi
# The install folder of a tool mise.toml pins (fails without mise, an untrusted mise.toml or the tool not installed).
marvin_mise_where() {
    local folder
    [ -n "$marvin_mise" ] || return 1
    folder="$(cd "$MARVIN_GODOT_ROOT" && "$marvin_mise" where "$1" < /dev/null 2> /dev/null)" || return 1
    [ -n "$folder" ] || return 1
    if command -v cygpath > /dev/null 2>&1; then folder="$(cygpath -u "$folder")"; fi
    printf '%s\n' "$folder"
}
if [ -z "$DOTNET_ROOT" ] && marvin_folder="$(marvin_mise_where dotnet)"; then
    if [ -f "$marvin_folder/dotnet" ] || [ -f "$marvin_folder/dotnet.exe" ]; then export DOTNET_ROOT="$marvin_folder"; fi
fi
if [ -z "$DOTNET_ROOT" ] && [ -d "$HOME/.dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; fi
# Git Bash on Windows: DOTNET_ROOT is a Windows path (C:\Program Files\dotnet), whose colon would split PATH.
dotnet_dir="$DOTNET_ROOT"
if [ -n "$dotnet_dir" ] && command -v cygpath > /dev/null 2>&1; then dotnet_dir="$(cygpath -u "$dotnet_dir")"; fi
export PATH="${dotnet_dir:+$dotnet_dir:}/opt/homebrew/bin:$PATH"
if [ -z "$GODOT" ] && marvin_folder="$(marvin_mise_where godot-mono)"; then
    for marvin_candidate in "$marvin_folder/Godot_mono.app/Contents/MacOS/Godot" \
        "$marvin_folder"/Godot_v*_mono_*/Godot_v*_mono_*_console.exe "$marvin_folder"/Godot_v*_mono_linux*/Godot_v*_mono_linux.*; do
        if [ -f "$marvin_candidate" ]; then export GODOT="$marvin_candidate"; break; fi
    done
fi
export GODOT="${GODOT:-$HOME/Applications/Godot_mono.app/Contents/MacOS/Godot}"
unset marvin_folder marvin_candidate
