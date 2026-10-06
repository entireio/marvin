#!/bin/bash
# Build the patched Godot 4.7.2 .NET engine for the Marvin port: the macOS editor (with its .NET glue and assemblies)
# and the export templates for macOS (universal) and Windows x86_64 (cross-compiled from macOS with llvm-mingw).
# See engine/README.md for what the patches do and how tools/godot and tools/export pick this engine.
#
#   engine/build-engine.sh all                # every step below, in order
#   engine/build-engine.sh deps               # scons (venv), llvm-mingw, D3D12/AccessKit/WinRT deps, MoltenVK check
#   engine/build-engine.sh source             # clone 4.7.2-stable into $GODOT_SRC and apply engine/patches (git am)
#   engine/build-engine.sh editor             # macOS arm64 editor + C# glue + GodotSharp assemblies -> .app bundle
#   engine/build-engine.sh templates-macos    # template_release/template_debug arm64 (+ x86_64) -> macos.zip
#   engine/build-engine.sh templates-windows  # template_release/template_debug x86_64 (+ console wrappers)
#   engine/build-engine.sh install            # editor -> $MARVIN_ENGINE_APP, templates -> export_templates/4.7.2.stable.marvin.mono
#   engine/build-engine.sh verify             # versions of the installed editor and templates
#
# Environment (defaults in brackets):
#   GODOT_SRC          Godot source checkout [~/src/godot-4.7.2-marvin]
#   MARVIN_ENGINE_APP  where `install` puts the editor [~/Applications/GodotMarvin/Godot_mono.app] (keep the app named
#                      Godot_mono.app: tools/perf/run-benchmark.py --wait-idle recognises Godot processes by that name)
#   JOBS               parallel compile jobs [all cores]; other GPU benchmarks on this Mac need an idle machine
#   LTO                none | auto | thin | full [none; production builds of official Godot use auto]
#   MACOS_ARCHS        "arm64 x86_64" [both]; "arm64" skips the Intel slice (the template is then Apple Silicon only)
#   LLVM_MINGW         llvm-mingw toolchain directory [~/src/llvm-mingw]; `deps` downloads the latest release there
#   VULKAN_SDK_PATH    MoltenVK/Vulkan SDK for the macOS x86_64 slice (Metal needs arm64) [auto-detect ~/VulkanSDK/*]
#
# Nothing here touches the stock editor (~/Applications/Godot_mono.app) or the stock templates (4.7.2.stable.mono).
set -euo pipefail

ENGINE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GODOT_ROOT_DIR="$(cd "$ENGINE_DIR/.." && pwd)"   # apps/simulator-godot
PATCH_DIR="$ENGINE_DIR/patches"
BUILD_DIR="$ENGINE_DIR/build"                     # gitignored: logs, venv, packaged .tpz
LOG_DIR="$BUILD_DIR/logs"

GODOT_SRC="${GODOT_SRC:-$HOME/src/godot-4.7.2-marvin}"
GODOT_TAG="4.7.2-stable"
GODOT_REPO="https://github.com/godotengine/godot.git"
MARVIN_ENGINE_APP="${MARVIN_ENGINE_APP:-$HOME/Applications/GodotMarvin/Godot_mono.app}"
JOBS="${JOBS:-$(sysctl -n hw.ncpu)}"
LTO="${LTO:-none}"
MACOS_ARCHS="${MACOS_ARCHS:-arm64 x86_64}"
LLVM_MINGW="${LLVM_MINGW:-$HOME/src/llvm-mingw}"
TEMPLATE_VERSION="4.7.2.stable.marvin.mono"       # version.py (patch 0001): 4.7.2 + status + module_config + .mono
TEMPLATES_DIR="$HOME/Library/Application Support/Godot/export_templates/$TEMPLATE_VERSION"
STOCK_TEMPLATES_DIR="$HOME/Library/Application Support/Godot/export_templates/4.7.2.stable.mono"
VENV="$BUILD_DIR/venv"

# .NET (DOTNET_ROOT, PATH) exactly as the project's own tools set it up (env.sh reads unset variables).
set +u
source "$GODOT_ROOT_DIR/tools/env.sh"
set -u

mkdir -p "$LOG_DIR"

say() { printf '\n== %s\n' "$*"; }
die() { printf 'build-engine.sh: %s\n' "$*" >&2; exit 1; }

scons_cmd() {
    # Runs scons from the venv in the Godot source tree, logging to $LOG_DIR/<name>.log.
    local name="$1"; shift
    say "scons $*"
    (cd "$GODOT_SRC" && "$VENV/bin/scons" -j"$JOBS" "$@") > "$LOG_DIR/$name.log" 2>&1 \
        || { tail -n 60 "$LOG_DIR/$name.log" >&2; die "scons failed, see $LOG_DIR/$name.log"; }
}

common_flags() {
    # Release-quality builds like the official ones: optimised, no debug symbols, the .NET module.
    echo "production=yes lto=$LTO module_mono_enabled=yes"
}

find_moltenvk() {
    if [ -n "${VULKAN_SDK_PATH:-}" ]; then echo "$VULKAN_SDK_PATH"; return; fi
    local d
    for d in "$HOME"/VulkanSDK/*; do
        [ -f "$d/macOS/lib/MoltenVK.xcframework/macos-arm64_x86_64/libMoltenVK.a" ] && { echo "$d"; return; }
    done
    echo ""
}

step_deps() {
    say "Build dependencies"
    command -v python3 > /dev/null || die "python3 is required"
    command -v git > /dev/null || die "git is required"
    xcode-select -p > /dev/null 2>&1 || die "Xcode or the Command Line Tools are required (xcode-select --install)"
    command -v dotnet > /dev/null || die ".NET 8 SDK not found (tools/env.sh looks in ~/.dotnet)"

    # SCons in a private venv (no system pip changes).
    if [ ! -x "$VENV/bin/scons" ]; then
        python3 -m venv "$VENV"
        "$VENV/bin/pip" install --quiet --upgrade pip scons
    fi
    "$VENV/bin/scons" --version | head -n 2

    # llvm-mingw for the Windows templates (macOS universal host build of the latest release).
    if [ ! -x "$LLVM_MINGW/bin/x86_64-w64-mingw32-clang" ]; then
        say "Downloading llvm-mingw into $LLVM_MINGW"
        local url
        url=$(python3 - <<'PY'
import json, urllib.request
rel = json.load(urllib.request.urlopen("https://api.github.com/repos/mstorsjo/llvm-mingw/releases/latest"))
assets = [a["browser_download_url"] for a in rel["assets"] if a["name"].endswith("-ucrt-macos-universal.tar.xz")]
print(assets[0] if assets else "")
PY
)
        [ -n "$url" ] || die "no llvm-mingw macOS release found; set LLVM_MINGW to an existing toolchain"
        mkdir -p "$LLVM_MINGW"
        curl -L --fail "$url" | tar -xJ -C "$LLVM_MINGW" --strip-components 1
        xattr -dr com.apple.quarantine "$LLVM_MINGW" 2> /dev/null || true
    fi
    "$LLVM_MINGW/bin/x86_64-w64-mingw32-clang" --version | head -n 1

    # Godot's own dependency scripts put their downloads in $GODOT_SRC/bin/build_deps when cross-compiling.
    if [ -d "$GODOT_SRC" ]; then
        if [ ! -d "$GODOT_SRC/bin/build_deps/mesa-x86_64-llvm" ]; then
            say "D3D12 dependencies (Mesa NIR, Agility SDK, PIX) for Windows"
            (cd "$GODOT_SRC" && PATH="$LLVM_MINGW/bin:$PATH" python3 misc/scripts/install_d3d12_sdk_windows.py --mingw_prefix="$LLVM_MINGW")
        fi
        # Optional, as in the official builds: screen reader support and WinRT text-to-speech (warnings without them).
        [ -d "$GODOT_SRC/bin/build_deps/accesskit" ] || (cd "$GODOT_SRC" && python3 misc/scripts/install_accesskit.py) || true
        [ -d "$GODOT_SRC/bin/build_deps/winrt_mingw" ] || (cd "$GODOT_SRC" && python3 misc/scripts/install_winrt.py) || true
    else
        echo "(run 'source' first for the D3D12 dependencies, then 'deps' again)"
    fi

    # MoltenVK only matters for the Intel (x86_64) slice of the macOS templates: Godot's Metal driver is arm64-only.
    if [[ " $MACOS_ARCHS " == *" x86_64 "* ]] && [ -z "$(find_moltenvk)" ]; then
        echo "MoltenVK not found. For the x86_64 macOS slice install the Vulkan SDK"
        echo "  (brew install jq && sh $GODOT_SRC/misc/scripts/install_vulkan_sdk_macos.sh)"
        echo "or build Apple Silicon only templates with MACOS_ARCHS=arm64."
    fi
}

step_source() {
    say "Godot $GODOT_TAG source in $GODOT_SRC"
    if [ ! -d "$GODOT_SRC/.git" ]; then
        mkdir -p "$(dirname "$GODOT_SRC")"
        git clone --depth 1 --branch "$GODOT_TAG" "$GODOT_REPO" "$GODOT_SRC"
    fi
    grep -q '^patch = 2$' "$GODOT_SRC/version.py" && grep -q '^minor = 7$' "$GODOT_SRC/version.py" \
        || die "$GODOT_SRC is not Godot 4.7.2"

    # Apply the series once, on a branch, so `git log` shows exactly what differs from the tag.
    local branch="marvin-4.7.2"
    if [ "$(git -C "$GODOT_SRC" rev-parse --abbrev-ref HEAD)" != "$branch" ]; then
        git -C "$GODOT_SRC" checkout -B "$branch"
    fi
    local patch subject
    for patch in "$PATCH_DIR"/*.patch; do
        subject=$(sed -n 's/^Subject: \[PATCH [0-9]*\/[0-9]*\] //p' "$patch" | head -n 1)
        if git -C "$GODOT_SRC" log --format=%s | grep -qxF "$subject"; then
            echo "already applied: $subject"
            continue
        fi
        echo "applying: $subject"
        git -C "$GODOT_SRC" -c user.name="Marvin engine build" -c user.email="marvin@localhost" am --3way --keep-cr "$patch" \
            || { git -C "$GODOT_SRC" am --abort || true; die "patch did not apply: $patch"; }
    done
    git -C "$GODOT_SRC" log --oneline -n 6
}

step_editor() {
    [ -x "$VENV/bin/scons" ] || die "run 'deps' first"
    # 1. The editor binary (Metal; MoltenVK for Vulkan when installed).
    local vulkan
    vulkan="$(macos_vulkan_flags arm64)"
    # shellcheck disable=SC2046
    scons_cmd editor-arm64 platform=macos target=editor arch=arm64 $(common_flags) $vulkan

    # 2. The C# glue for this engine's API (adds the new RenderingServer method), then the GodotSharp assemblies.
    local bin="$GODOT_SRC/bin/godot.macos.editor.arm64.mono"
    [ -x "$bin" ] || die "editor binary not found: $bin"
    say "Generating the C# glue"
    "$bin" --headless --generate-mono-glue "$GODOT_SRC/modules/mono/glue" > "$LOG_DIR/glue.log" 2>&1 \
        || { tail -n 40 "$LOG_DIR/glue.log" >&2; die "glue generation failed"; }
    say "Building the GodotSharp assemblies"
    # No --push-nupkgs-local: the project keeps restoring the stock Godot.NET.Sdk/GodotSharp 4.7.2 from nuget.org
    # (this engine only adds API, which the facade calls dynamically), and these 4.7.2-versioned packages must never
    # shadow the stock ones in the NuGet cache.
    (cd "$GODOT_SRC" && python3 modules/mono/build_scripts/build_assemblies.py --godot-output-dir=bin --godot-platform=macos) \
        > "$LOG_DIR/assemblies.log" 2>&1 || { tail -n 40 "$LOG_DIR/assemblies.log" >&2; die "assembly build failed"; }
    rm -rf "$GODOT_SRC/bin/GodotSharp/Tools/nupkgs"

    # 3. The .app bundle (Godot's own bundler: lipo, Info.plist, GodotSharp in Contents/Resources).
    # shellcheck disable=SC2046
    scons_cmd editor-bundle platform=macos target=editor arch=arm64 $(common_flags) $vulkan generate_bundle=yes
    [ -d "$GODOT_SRC/bin/godot_macos_editor_mono.app" ] || die "editor bundle not found"
}

macos_vulkan_flags() {
    # Metal is arm64-only in Godot; the x86_64 slice renders Forward+ through Vulkan (MoltenVK, linked statically).
    local arch="$1" mvk
    mvk="$(find_moltenvk)"
    if [ -n "$mvk" ]; then
        echo "vulkan=yes vulkan_sdk_path=$mvk"
    elif [ "$arch" = x86_64 ]; then
        die "the x86_64 slice needs MoltenVK (see 'deps'), or set MACOS_ARCHS=arm64"
    else
        echo "vulkan=no"
    fi
}

step_templates_macos() {
    [ -x "$VENV/bin/scons" ] || die "run 'deps' first"
    local arch target vulkan
    for arch in $MACOS_ARCHS; do
        vulkan="$(macos_vulkan_flags "$arch")"
        for target in template_release template_debug; do
            # shellcheck disable=SC2046
            scons_cmd "macos-$target-$arch" platform=macos target=$target arch="$arch" $(common_flags) $vulkan
        done
    done
    # Lipo the architectures and zip macos_template.app (bin/godot_macos_mono.zip, Godot's own template bundler). The
    # flags repeat the first architecture's build exactly, so this call only bundles.
    local first_arch="${MACOS_ARCHS%% *}"
    vulkan="$(macos_vulkan_flags "$first_arch")"
    # shellcheck disable=SC2046
    scons_cmd macos-template-bundle platform=macos target=template_release arch="$first_arch" $(common_flags) $vulkan generate_bundle=yes
    [ -f "$GODOT_SRC/bin/godot_macos_mono.zip" ] || die "bin/godot_macos_mono.zip not found"
}

step_templates_windows() {
    [ -x "$VENV/bin/scons" ] || die "run 'deps' first"
    [ -x "$LLVM_MINGW/bin/x86_64-w64-mingw32-clang" ] || die "llvm-mingw not found in $LLVM_MINGW (run 'deps')"
    [ -d "$GODOT_SRC/bin/build_deps/mesa-x86_64-llvm" ] || die "D3D12 dependencies missing (run 'deps' after 'source')"
    export PATH="$LLVM_MINGW/bin:$PATH"
    local target
    for target in template_release template_debug; do
        # Direct3D 12 (the preset's driver) and Vulkan (its fallback, through volk: no SDK needed); the GUI exe and its
        # .console.exe wrapper. The .NET module only loads hostfxr at run time, so no Windows .NET packs are needed.
        # shellcheck disable=SC2046
        scons_cmd "windows-$target" platform=windows target=$target arch=x86_64 \
            $(common_flags) use_mingw=yes use_llvm=yes mingw_prefix="$LLVM_MINGW" d3d12=yes vulkan=yes
    done
}

step_install() {
    say "Installing the editor in $MARVIN_ENGINE_APP"
    [ -d "$GODOT_SRC/bin/godot_macos_editor_mono.app" ] || die "build the editor first"
    mkdir -p "$(dirname "$MARVIN_ENGINE_APP")"
    rm -rf "$MARVIN_ENGINE_APP"
    cp -R "$GODOT_SRC/bin/godot_macos_editor_mono.app" "$MARVIN_ENGINE_APP"
    # Ad-hoc signature for the bundle (the linker signed the binary only); no hardened runtime, so .NET can JIT.
    codesign --force --deep --sign - "$MARVIN_ENGINE_APP"

    say "Installing export templates in $TEMPLATES_DIR"
    mkdir -p "$TEMPLATES_DIR"
    printf '%s' "$TEMPLATE_VERSION" > "$TEMPLATES_DIR/version.txt"
    if [ -f "$GODOT_SRC/bin/godot_macos_mono.zip" ]; then
        cp "$GODOT_SRC/bin/godot_macos_mono.zip" "$TEMPLATES_DIR/macos.zip"
    else
        echo "(no macOS templates built)"
    fi
    local target exe console
    for target in release debug; do
        exe=$(ls "$GODOT_SRC"/bin/godot.windows.template_${target}.x86_64*.mono.exe 2> /dev/null | grep -v '\.console\.exe$' | head -n 1 || true)
        console=$(ls "$GODOT_SRC"/bin/godot.windows.template_${target}.x86_64*.mono.console.exe 2> /dev/null | head -n 1 || true)
        if [ -n "$exe" ]; then
            cp "$exe" "$TEMPLATES_DIR/windows_${target}_x86_64.exe"
            [ -n "$console" ] && cp "$console" "$TEMPLATES_DIR/windows_${target}_x86_64_console.exe"
        else
            echo "(no Windows $target template built)"
        fi
    done
    # ICU data for the text server, as in the official template set.
    if [ -f "$STOCK_TEMPLATES_DIR/icudt_godot.dat" ]; then
        cp "$STOCK_TEMPLATES_DIR/icudt_godot.dat" "$TEMPLATES_DIR/"
    fi

    # A .tpz of the same set, for installing on another machine (Editor > Manage Export Templates > Install from File).
    mkdir -p "$BUILD_DIR"
    rm -rf "$BUILD_DIR/templates" "$BUILD_DIR/Godot_v4.7.2-marvin_mono_export_templates.tpz"
    mkdir -p "$BUILD_DIR/templates"
    cp "$TEMPLATES_DIR"/* "$BUILD_DIR/templates/"
    (cd "$BUILD_DIR" && zip -q -r "Godot_v4.7.2-marvin_mono_export_templates.tpz" templates)
    rm -rf "$BUILD_DIR/templates"
    echo "packed $BUILD_DIR/Godot_v4.7.2-marvin_mono_export_templates.tpz"
}

step_verify() {
    say "Installed engine"
    local godot="$MARVIN_ENGINE_APP/Contents/MacOS/Godot"
    [ -x "$godot" ] || die "no editor at $godot"
    local version
    version=$("$godot" --headless --version 2> /dev/null | tail -n 1 | tr -d '\r')
    echo "editor:    $version"
    [[ "$version" == 4.7.2.stable.marvin.mono.* ]] || die "unexpected version $version (expected 4.7.2.stable.marvin.mono.*)"
    echo "templates: $(cat "$TEMPLATES_DIR/version.txt" 2> /dev/null || echo missing) in $TEMPLATES_DIR"
    ls -la "$TEMPLATES_DIR"
    echo
    echo "Use it with:  MARVIN_ENGINE=marvin apps/simulator-godot/tools/godot ...   (and tools/export)"
}

case "${1:-}" in
    deps) step_deps;;
    source) step_source;;
    editor) step_editor;;
    templates-macos) step_templates_macos;;
    templates-windows) step_templates_windows;;
    install) step_install;;
    verify) step_verify;;
    all) step_deps; step_source; step_deps; step_editor; step_templates_macos; step_templates_windows; step_install; step_verify;;
    -h|--help|"") sed -n '2,26p' "$0";;
    *) die "unknown step '$1' (try --help)";;
esac
