#!/usr/bin/env python3
"""The Godot port's developer tasks, the same on macOS and Windows. mise.toml runs them (README.md, "Setup"):

    mise run setup                      # Git LFS files, the game's assets, C# build, Godot import (idempotent)
    mise run play [-- FLAGS]            # build, then the game; FLAGS go to the game (Godot's own before a second --)
    mise run test [-- MODES|all]        # quick headless smoke (tools/ci/headless-modes.py)
    mise run checks [-- ARGS]           # the simulation checks (no Godot)
    mise run export [-- macos|windows] [--zip] [--debug]   # release builds; fetches the export templates when missing
    mise run build                      # the C#
    mise run godot -- ARGS              # Godot on this project with any arguments

Without mise: python tools/dev.py TASK [ARGS]. Godot and .NET are $GODOT and $DOTNET_ROOT when set, otherwise the
versions mise.toml pins (`mise where`), otherwise ~/Applications/Godot_mono.app and ~/.dotnet, the order tools/env.sh
uses for the bash scripts. Standard library only.
"""
import http.client
import io
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent  # apps/simulator-godot
REPO = ROOT.parent.parent
MAC_RESOURCES = REPO / "apps" / "simulator-macos" / "Resources"
MARVIN_STEP = "hardware/mechanical/step/marvin_robot.step"
LFS_INCLUDE = f"apps/simulator-macos/Resources/**,{MARVIN_STEP}"  # what the game is made from
LFS_POINTER = b"version https://git-lfs.github.com/spec/"
GODOT_VERSION = "4.7.2.stable.mono"
SMOKE_MODES = "town-statistics,character-smoke-test"
WINDOWS, MACOS = os.name == "nt", sys.platform == "darwin"
EXE = ".exe" if WINDOWS else ""
PRESETS = {"macos": ("macOS", "build/macos/Marvin Simulator.app"),
           "windows": ("Windows Desktop", "build/windows/MarvinSimulator.exe")}
# What each preset needs from the export templates besides version.txt and the ICU data.
TEMPLATES = {("macos", "release"): ["macos.zip"], ("macos", "debug"): ["macos.zip"],
             ("windows", "release"): ["windows_release_x86_64.exe", "windows_release_x86_64_console.exe"],
             ("windows", "debug"): ["windows_debug_x86_64.exe", "windows_debug_x86_64_console.exe"]}


def say(message):
    print(f"==> {message}", flush=True)


def die(message):
    print(f"dev.py: {message}", file=sys.stderr, flush=True)
    sys.exit(1)


def run(cmd, check=True, **kwargs):
    cmd = [str(c) for c in cmd]
    try:
        proc = subprocess.run(cmd, **kwargs)
    except KeyboardInterrupt:
        sys.exit(130)
    if check and proc.returncode != 0:
        die(f"{' '.join([Path(cmd[0]).name, *cmd[1:3]])} ... failed (exit {proc.returncode})")
    return proc


def tail(path, lines=40):
    return "\n".join(Path(path).read_text(encoding="utf-8", errors="replace").splitlines()[-lines:])


# --- Tools -------------------------------------------------------------------------------------------------------------

def mise_where(tool):
    """The install folder of a tool that mise.toml pins, or None (no mise, config not trusted, tool not installed)."""
    mise = shutil.which("mise")
    if not mise:
        return None
    try:
        proc = subprocess.run([mise, "where", tool], cwd=ROOT, stdin=subprocess.DEVNULL, capture_output=True,
                              text=True, timeout=120)
    except (OSError, subprocess.SubprocessError):
        return None
    lines = proc.stdout.strip().splitlines()
    folder = Path(lines[-1].strip()) if proc.returncode == 0 and lines else None
    return folder if folder and folder.is_dir() else None


def godot_in(folder):
    """The Godot .NET editor binary in an unpacked release (on Windows the console variant, which prints the log)."""
    if MACOS:
        candidates = [folder / "Godot_mono.app" / "Contents" / "MacOS" / "Godot"]
    else:
        pattern = "Godot_v*_mono_*_console.exe" if WINDOWS else "Godot_v*_mono_linux*"
        candidates = [c for c in sorted(folder.glob(pattern)) + sorted(folder.glob(f"*/{pattern}"))
                      if WINDOWS or re.search(r"\.(x86_64|arm64)$", c.name)]
    return next((c for c in candidates if c.is_file()), None)


def find_godot():
    if os.environ.get("GODOT"):
        godot = Path(os.environ["GODOT"])
        if not godot.is_file():
            die(f"GODOT is set to {godot}, which does not exist")
        return godot
    folder = mise_where("godot-mono")
    godot = godot_in(folder) if folder else None
    for apps in ([Path.home() / "Applications", Path("/Applications")] if MACOS and not godot else []):
        godot = godot or godot_in(apps)
    if not godot:
        die("Godot 4.7.2 .NET not found: run the tasks with mise (mise run setup installs it), "
            "or set GODOT to the editor binary (README.md)")
    return godot


def setup_dotnet():
    """Puts the .NET SDK on PATH and in DOTNET_ROOT, for dotnet and for Godot, which loads .NET from there."""
    root = os.environ.get("DOTNET_ROOT")
    if not root:
        for folder in (mise_where("dotnet"), Path.home() / ".dotnet"):
            if folder and (folder / f"dotnet{EXE}").is_file():
                root = str(folder)
                break
    if root:
        os.environ["DOTNET_ROOT"] = root
        paths = [os.path.normcase(os.path.normpath(p)) for p in os.environ.get("PATH", "").split(os.pathsep) if p]
        if os.path.normcase(os.path.normpath(root)) not in paths:
            os.environ["PATH"] = root + os.pathsep + os.environ.get("PATH", "")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        die(".NET SDK not found: run the tasks with mise (mise run setup installs it), or set DOTNET_ROOT")
    return dotnet


def godot_version(godot):
    proc = subprocess.run([str(godot), "--headless", "--version"], stdin=subprocess.DEVNULL, capture_output=True,
                          text=True, errors="replace", timeout=300)
    lines = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    return lines[-1] if lines else ""


# --- Setup -------------------------------------------------------------------------------------------------------------

def lfs_pointers():
    """The files the game is made from that are still Git LFS pointers (a clone made without Git LFS)."""
    pointers = []
    for path in [*MAC_RESOURCES.rglob("*"), REPO / MARVIN_STEP]:
        try:
            if path.is_file() and path.stat().st_size < 1024:
                with open(path, "rb") as f:
                    if f.read(len(LFS_POINTER)) == LFS_POINTER:
                        pointers.append(path)
        except OSError:
            pass
    return pointers


def fetch_lfs(pointers):
    say(f"Git LFS: fetching {len(pointers)} files (textures, mesh sources, Marvin's CAD master)")
    git = shutil.which("git") or die("git not found")
    if subprocess.run([git, "lfs", "version"], capture_output=True).returncode != 0:
        die("Git LFS not found: run the tasks with mise (it provides Git LFS), or install Git LFS")
    configured = subprocess.run([git, "config", "--get", "filter.lfs.process"], cwd=REPO, capture_output=True,
                                text=True).stdout.strip()
    config, hooks = [], None
    if not configured:
        # This git has no Git LFS (Apple's git on a new Mac). Use mise's git-lfs for this command only and leave
        # .git/config and .git/hooks alone, so that git keeps working where git-lfs is not on PATH (Git LFS's hooks,
        # which `git lfs pull` would install, stop `git push` without it).
        hooks = tempfile.mkdtemp(prefix="marvin-lfs-hooks-")
        config = ["-c", "filter.lfs.clean=git-lfs clean -- %f", "-c", "filter.lfs.smudge=git-lfs smudge -- %f",
                  "-c", "filter.lfs.process=git-lfs filter-process", "-c", "filter.lfs.required=true",
                  "-c", f"core.hooksPath={hooks}"]
    try:
        run([git, *config, "lfs", "pull", f"--include={LFS_INCLUDE}"], cwd=REPO)
        if config:
            # Index the files once more through the LFS filter a moment later: git, without the filter, would take
            # files written in the same second as its index for changed ones.
            time.sleep(1.5)
            subprocess.run([git, *config, "status", "--porcelain", "--untracked-files=no"], cwd=REPO,
                           capture_output=True)
    finally:
        if hooks:
            shutil.rmtree(hooks, ignore_errors=True)
    left = lfs_pointers()
    if left:
        die(f"Git LFS: {len(left)} files are still pointers, e.g. {left[0]}")
    if not configured:
        print("    (Your git has no Git LFS, so a later `git pull` leaves changed LFS files as pointers until the next "
              "`mise run setup`.)", flush=True)


def prepare_assets():
    env = dict(os.environ)
    if env.get("MARVIN_MAC_RESOURCES"):
        say(f"Assets: from MARVIN_MAC_RESOURCES={env['MARVIN_MAC_RESOURCES']}")
    else:
        pointers = lfs_pointers()
        if pointers:
            fetch_lfs(pointers)
        else:
            say("Git LFS: files present")
        say("Assets: the robots' meshes (scripts/prepare-simulator-assets.py, skipped when up to date)")
        run([sys.executable, REPO / "scripts" / "prepare-simulator-assets.py"], cwd=REPO)
        env["MARVIN_MAC_RESOURCES"] = str(MAC_RESOURCES)  # this checkout's, also in a git worktree
    say("Assets: into assets/ (tools/sync-assets.py)")
    run([sys.executable, ROOT / "tools" / "sync-assets.py"], cwd=ROOT, env=env)


def build():
    say("Build: C# (MarvinGodot, MarvinCore)")
    run([setup_dotnet(), "build", ROOT / "MarvinGodot.csproj", "-nologo", "-v", "q"], cwd=ROOT)


def import_project(godot, log_name="import.log"):
    first = not (ROOT / ".godot" / "imported").is_dir()
    say("Godot: importing textures and audio" + (" (the first time takes a few minutes)" if first else ""))
    log = ROOT / "build" / log_name
    log.parent.mkdir(exist_ok=True)
    with open(log, "wb") as f:
        proc = run([godot, "--headless", "--path", ROOT, "--import"], check=False, stdout=f, stderr=subprocess.STDOUT)
    if proc.returncode != 0:
        print(tail(log), file=sys.stderr)
        die(f"Godot import failed (exit {proc.returncode}), log: {log}")


def is_set_up():
    return (ROOT / "assets" / "Marvin" / "geometry.bin").is_file() and (ROOT / ".godot" / "imported").is_dir()


def cmd_setup(args):
    if args:
        die("setup takes no arguments")
    start = time.time()
    godot, dotnet = find_godot(), setup_dotnet()
    version = godot_version(godot)
    sdk = subprocess.run([dotnet, "--version"], capture_output=True, text=True).stdout.strip()
    print(f"    Godot  {version or '?'}  {godot}\n    .NET   {sdk or '?'}  {dotnet}\n"
          f"    Python {platform.python_version()}  {sys.executable}", flush=True)
    if not version.startswith(GODOT_VERSION):
        print(f"    warning: the port is made for Godot {GODOT_VERSION} (mise.toml)", flush=True)
    prepare_assets()
    build()
    import_project(godot)
    say(f"Ready ({time.time() - start:.0f} s). Play: mise run play")
    return 0


def ready():
    """Godot, after setting up what is missing and building the C# (the game runs the last build)."""
    if is_set_up():
        build()
    else:
        say("Not set up yet: running setup first")
        cmd_setup([])
    return find_godot()


# --- Play, test, checks ------------------------------------------------------------------------------------------------

def cmd_play(args):
    godot = ready()
    # Flags are the game's (after "--", OS.GetCmdlineUserArgs); with a "--" of their own, Godot's come before it.
    args = args if not args or "--" in args else ["--", *args]
    return run([godot, "--path", ROOT, *args], check=False).returncode


def cmd_godot(args):
    setup_dotnet()
    return run([find_godot(), "--path", ROOT, *args], check=False).returncode


def cmd_build(args):
    build()
    return 0


def cmd_test(args):
    if len(args) > 1 or (args and args[0].startswith("-")):
        die("test takes one argument: comma-separated modes of tools/ci/headless-modes.py, or all")
    modes = args[0] if args else SMOKE_MODES
    godot = ready()
    say(f"Smoke test, headless: {'every mode' if modes == 'all' else modes}")
    cmd = [sys.executable, ROOT / "tools" / "ci" / "headless-modes.py", "--godot", godot, "--out", ROOT / "build" / "test",
           "--title", "Quick smoke test (headless) against Godot on macOS"]
    return run(cmd + ([] if modes == "all" else ["--only", modes]), check=False, cwd=ROOT).returncode


def cmd_checks(args):
    dotnet = setup_dotnet()
    return run([dotnet, "run", "--project", ROOT / "src" / "Checks" / "MarvinChecks.csproj", "-c", "Release", "-nologo",
                "--", *args], check=False, cwd=ROOT).returncode


# --- Export ------------------------------------------------------------------------------------------------------------

class RangeReader(io.RawIOBase):
    """A remote file read with HTTP range requests, so that zipfile downloads only the members it extracts."""

    def __init__(self, url):
        self.url, self.pos = url, 0
        with self._get(0, 0) as response:
            if response.status != 206:
                raise OSError(f"HTTP {response.status} to a range request")
            self.size = int(response.headers["Content-Range"].rsplit("/", 1)[1])

    def _get(self, first, last):
        request = urllib.request.Request(self.url, headers={"Range": f"bytes={first}-{last}",
                                                            "User-Agent": "marvin-simulator-godot"})
        return urllib.request.urlopen(request, timeout=60)

    def readable(self):
        return True

    def seekable(self):
        return True

    def tell(self):
        return self.pos

    def seek(self, offset, whence=io.SEEK_SET):
        self.pos = {io.SEEK_SET: 0, io.SEEK_CUR: self.pos, io.SEEK_END: self.size}[whence] + offset
        return self.pos

    def readinto(self, buffer):
        end = min(self.pos + len(buffer), self.size)
        if end <= self.pos:
            return 0
        for attempt in range(5):
            try:
                with self._get(self.pos, end - 1) as response:
                    if response.status != 206:
                        raise OSError(f"HTTP {response.status} to a range request")
                    data = response.read()
                break
            except (OSError, http.client.HTTPException):
                if attempt == 4:
                    raise
                time.sleep(2 ** attempt)
        buffer[:len(data)] = data
        self.pos += len(data)
        return len(data)


def templates_folder(template_version):
    if MACOS:
        base = Path.home() / "Library" / "Application Support" / "Godot"
    elif WINDOWS:
        base = Path(os.environ.get("APPDATA") or Path.home() / "AppData" / "Roaming") / "Godot"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / "godot"
    return base / "export_templates" / template_version


def ensure_templates(folder, template_version, needed):
    """Fetches the export template files the presets need from Godot's release archive (1.2 GB for every platform;
    only the needed members, about 200 MB for both presets, are downloaded)."""
    missing = [name for name in ["icudt_godot.dat", *needed, "version.txt"] if not (folder / name).is_file()]
    if not missing:
        return
    match = re.match(r"(\d+\.\d+(?:\.\d+)?)\.([a-z0-9]+)\.mono$", template_version)
    if not match:
        die(f"unexpected Godot version {template_version}")
    tag = f"{match[1]}-{match[2]}"
    url = f"https://github.com/godotengine/godot-builds/releases/download/{tag}/Godot_v{tag}_mono_export_templates.tpz"
    say(f"Export templates {template_version}: fetching {', '.join(missing)} into {folder}")
    folder.mkdir(parents=True, exist_ok=True)
    whole = None
    try:
        source = io.BufferedReader(RangeReader(url), buffer_size=8 << 20)
    except (OSError, http.client.HTTPException) as error:
        print(f"    no range requests ({error}): downloading the whole archive", flush=True)
        fd, whole = tempfile.mkstemp(suffix=".tpz")
        with os.fdopen(fd, "wb") as f, urllib.request.urlopen(url, timeout=60) as response:
            shutil.copyfileobj(response, f, 1 << 20)
        source = open(whole, "rb")
    try:
        with zipfile.ZipFile(source) as archive:
            for name in missing:  # version.txt last: Godot takes the folder for complete once it is there
                info = archive.getinfo(f"templates/{name}")
                print(f"    {name} ({info.file_size / 1e6:.1f} MB)", flush=True)
                partial = folder / f"{name}.part"
                with archive.open(info) as src, open(partial, "wb") as dst:  # zipfile checks the CRC
                    shutil.copyfileobj(src, dst, 1 << 20)
                os.replace(partial, folder / name)
    finally:
        source.close()
        if whole:
            os.unlink(whole)


EXPORT_HELP = """mise run export [-- macos|windows] [--zip] [--debug]   (python tools/dev.py export ...)
  Release builds: build/macos/Marvin Simulator.app, build/windows/MarvinSimulator.exe (default: both on a Mac,
  windows elsewhere), checked by tools/export-verify.py. --zip also packs build/MarvinSimulator-windows-x86_64.zip to
  copy to a PC; --debug uses the debug engine template (same C#, ExportDebug). Fetches the Godot export templates when
  they are missing."""


def cmd_export(args):
    platforms, mode, pack = [], "release", False
    for arg in args:
        if arg in PRESETS:
            platforms.append(arg)
        elif arg == "--debug":
            mode = "debug"
        elif arg == "--zip":
            pack = True
        elif arg in ("-h", "--help"):
            print(EXPORT_HELP)
            return 0
        else:
            die(f"export: unknown argument {arg}\n{EXPORT_HELP}")
    platforms = platforms or (["macos", "windows"] if MACOS else ["windows"])
    if not (ROOT / "assets" / "Marvin" / "geometry.bin").is_file():
        die("assets/ is missing: run mise run setup first")
    setup_dotnet()
    godot = find_godot()
    version = godot_version(godot)
    match = re.match(r"(\d+\.\d+(?:\.\d+)?\.[a-z0-9]+\.mono)", version)
    if not match:
        die(f"{godot} is not a Godot .NET editor (version {version!r})")
    folder = templates_folder(match[1])
    ensure_templates(folder, match[1], sorted({n for p in platforms for n in TEMPLATES[(p, mode)]}))

    # Imports first: the export packs the imported textures and the raw .wav files (importer "keep", sync-assets.py).
    import_project(godot)
    status = 0
    for name in platforms:
        preset, out = PRESETS[name]
        folder_out = (ROOT / out).parent
        shutil.rmtree(folder_out, ignore_errors=True)
        folder_out.mkdir(parents=True)
        log = ROOT / "build" / f"export-{name}.log"
        say(f"Exporting {preset} ({mode}) -> {out}")
        with open(log, "wb") as f:
            proc = run([godot, "--headless", "--path", ROOT, f"--export-{mode}", preset, ROOT / out], check=False,
                       stdout=f, stderr=subprocess.STDOUT)
        if proc.returncode != 0:
            print(tail(log), file=sys.stderr)
            print(f"dev.py: {preset} export failed, log: {log}", file=sys.stderr)
            status = 1
            continue
        errors = [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                  if re.search(r"(^|m)(ERROR|SCRIPT ERROR):", line)]
        if errors:
            print("\n".join(errors), file=sys.stderr)
            print(f"dev.py: {preset} export logged errors, log: {log}", file=sys.stderr)
            status = 1
        if run([sys.executable, ROOT / "tools" / "export-verify.py", folder_out], check=False, cwd=ROOT).returncode:
            status = 1
        if pack and name == "windows":
            archive = ROOT / "build" / "MarvinSimulator-windows-x86_64"
            Path(f"{archive}.zip").unlink(missing_ok=True)
            shutil.make_archive(str(archive), "zip", folder_out)
            print("Packed build/MarvinSimulator-windows-x86_64.zip", flush=True)
    return status


TASKS = {"setup": cmd_setup, "play": cmd_play, "test": cmd_test, "checks": cmd_checks, "export": cmd_export,
         "build": cmd_build, "godot": cmd_godot}


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in TASKS:
        print(__doc__.strip())
        return 0 if len(sys.argv) > 1 and sys.argv[1] in ("-h", "--help") else 2
    return TASKS[sys.argv[1]](sys.argv[2:])


if __name__ == "__main__":
    sys.exit(main())
