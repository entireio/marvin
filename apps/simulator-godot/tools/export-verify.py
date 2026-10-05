#!/usr/bin/env python3
"""Check the layout of an exported build (tools/export runs it after each export).

    tools/export-verify.py build/macos      # Marvin Simulator.app
    tools/export-verify.py build/windows    # MarvinSimulator.exe, .console.exe, .pck, data_MarvinGodot_windows_x86_64/

Checks, without running the build (so the Windows export can be checked on a Mac):
- the executables: architecture (macOS universal arm64 + x86_64; Windows PE32+ x86-64, GUI exe plus console
  wrapper), the app bundle's Info.plist and icon, the Windows exe's icon and version resources;
- the .NET data folder(s): the self-contained runtime for that platform (coreclr, hostfxr, hostpolicy, clrjit for
  win-x64 / osx-arm64 / osx-x64), the game assemblies built as ExportRelease (or ExportDebug), no editor assemblies;
- the native libraries the game assemblies name: only the two macOS-only bindings, both guarded by an OS check
  (ProcessInfo.thermalState, Darwin hypot); anything else would be a macOS-only code path;
- the pack: every raw asset the game reads with FileAccess (geometry.bin, the JSON meshes and manifests, every .wav)
  byte-identical to assets/ (the pack's MD5s), every imported texture, the main scene, and nothing from
  reference/, tools/, docs/, captures/, build/ or src/.
Exit status 1 when a check fails.
"""
import hashlib, json, os, plistlib, re, struct, sys, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
failures = []

def check(ok, what, detail=""):
    print(("PASS " if ok else "FAIL ") + what + (f": {detail}" if detail else ""))
    if not ok: failures.append(what)
    return ok

# ---------------------------------------------------------------- PCK (Godot 4 pack, format 2-4)
def read_pck(path):
    with open(path, "rb") as f:
        magic, version, major, minor, patch = struct.unpack("<4sIIII", f.read(20))
        if magic != b"GDPC": raise ValueError("not a Godot pack")
        flags, file_base = struct.unpack("<IQ", f.read(12))
        if version >= 3:
            dir_offset, = struct.unpack("<Q", f.read(8))
        else:
            f.read(16 * 4); dir_offset = f.tell()
        if flags & 1: raise ValueError("encrypted directory")
        f.seek(dir_offset)
        count, = struct.unpack("<I", f.read(4))
        files = {}
        for _ in range(count):
            n, = struct.unpack("<I", f.read(4))
            name = f.read(n).rstrip(b"\0").decode("utf-8")
            offset, size = struct.unpack("<QQ", f.read(16))
            md5 = f.read(16).hex()
            f.read(4)
            files[name[6:] if name.startswith("res://") else name] = (size, md5)
        return (major, minor, patch, version), files

def check_pack(pck):
    try:
        (major, minor, patch, version), files = read_pck(pck)
    except Exception as e:
        return check(False, f"pack {pck.name} readable", str(e))
    check(True, f"pack {pck.name}", f"Godot {major}.{minor}.{patch}, pack format {version}, {len(files)} files, {pck.stat().st_size / 1e6:.0f} MB")
    assets = ROOT / "assets"
    raw = sorted(p for p in assets.rglob("*") if p.is_file() and p.suffix.lower() in {".bin", ".json", ".wav"})
    missing, differ = [], []
    for p in raw:
        key = p.relative_to(ROOT).as_posix()
        if key not in files: missing.append(key); continue
        size, md5 = files[key]
        if size != p.stat().st_size or md5 != hashlib.md5(p.read_bytes()).hexdigest(): differ.append(key)
    wavs = sum(1 for p in raw if p.suffix.lower() == ".wav")
    check(not raw == [], "raw assets present in assets/", f"{len(raw)} files" if raw else "run tools/sync-assets.py")
    check(not missing and not differ, "raw assets packed byte-identical (geometry.bin, JSON meshes, .wav)",
          f"{len(raw) - len(missing) - len(differ)} of {len(raw)} ({wavs} .wav)" + (f"; missing {missing[:5]}" if missing else "") + (f"; different {differ[:5]}" if differ else ""))
    textures, absent = 0, []
    for imp in sorted(assets.rglob("*.import")):
        text = imp.read_text()
        if 'importer="texture"' not in text or imp.name.endswith(".svg.import"): continue
        textures += 1
        m = re.search(r'^path="res://([^"]+)"', text, re.M)
        if not m or m.group(1) not in files or imp.relative_to(ROOT).as_posix() not in files: absent.append(imp.name)
    check(textures > 0 and not absent, "imported textures packed", f"{textures - len(absent)} of {textures}" + (f"; missing {absent[:5]}" if absent else ""))
    check("project.binary" in files and any(k.endswith("Main.scn") for k in files), "project settings and main scene packed")
    forbidden = [k for k in files if k.split("/")[0] in {"reference", "tools", "docs", "captures", "build", "src"} or k.endswith(".svg.import")]
    check(not forbidden, "no development files packed", ", ".join(forbidden[:5]))

# ---------------------------------------------------------------- .NET data folder
EDITOR_ASSEMBLIES = ("GodotSharpEditor", "GodotTools", "GodotPlugins.Editor")
# Native libraries the game assemblies may name: macOS-only, each behind an OS check.
ALLOWED_NATIVE = {
    "libobjc.A.dylib": "Foundation.cs ProcessInfo.thermalState (OperatingSystem.IsMacOS)",
    "libSystem.B.dylib": "Swift.cs Darwin hypot (macOS only; Swift.portableHypot elsewhere)",
}
def native_names(dll):
    data = dll.read_bytes()
    names = set()
    for pattern in (rb"[A-Za-z0-9_./+-]+\.(?:dylib|so(?:\.[0-9]+)?|framework)\b", rb"(?:[A-Za-z0-9_.+-]\x00)+\.\x00(?:d\x00y\x00l\x00i\x00b\x00|s\x00o\x00)"):
        for m in re.finditer(pattern, data):
            s = m.group(0).replace(b"\x00", b"").decode("ascii", "replace")
            names.add(s.split("/")[-1])
    return names

def pe_info(path):
    with open(path, "rb") as f:
        data = f.read(4096)
    if data[:2] != b"MZ": return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0": return None
    machine, = struct.unpack_from("<H", data, pe + 4)
    opt = pe + 24
    magic, = struct.unpack_from("<H", data, opt)
    subsystem, = struct.unpack_from("<H", data, opt + 68)
    dirs = opt + (112 if magic == 0x20B else 96)
    cli_rva, = struct.unpack_from("<I", data, dirs + 14 * 8)
    return {"machine": machine, "pe32plus": magic == 0x20B, "subsystem": subsystem, "managed": cli_rva != 0}

def check_dotnet(folder, rid, native_suffix, arch_check):
    check(folder.is_dir(), f".NET data folder {folder.name}")
    if not folder.is_dir(): return
    names = {p.name for p in folder.iterdir()}
    for a in ("MarvinGodot.dll", "MarvinCore.dll", "GodotSharp.dll", "System.Private.CoreLib.dll", "MarvinGodot.runtimeconfig.json", "MarvinGodot.deps.json"):
        check(a in names, f"{folder.name}: {a}")
    runtime = ["coreclr", "hostfxr", "hostpolicy", "clrjit"]
    natives = [(n + native_suffix) if native_suffix == ".dll" else ("lib" + n + native_suffix) for n in runtime]
    present = [n for n in natives if n in names]
    check(len(present) == len(natives), f"{folder.name}: self-contained .NET runtime ({', '.join(natives)})")
    bad_arch = [n for n in present if not arch_check(folder / n)]
    check(not bad_arch, f"{folder.name}: runtime built for {rid}", ", ".join(bad_arch))
    try:
        deps = json.loads((folder / "MarvinGodot.deps.json").read_text())
        target = deps.get("runtimeTarget", {}).get("name", "")
        check(target.endswith("/" + rid), f"{folder.name}: deps.json runtime target {rid}", target)
        runtime_cfg = json.loads((folder / "MarvinGodot.runtimeconfig.json").read_text())
        fw = runtime_cfg.get("runtimeOptions", {}).get("includedFrameworks") or runtime_cfg.get("runtimeOptions", {}).get("framework")
        check(bool(fw), f"{folder.name}: runtimeconfig.json frameworks", json.dumps(fw))
    except Exception as e:
        check(False, f"{folder.name}: deps/runtimeconfig readable", str(e))
    editor = sorted(n for n in names if n.startswith(EDITOR_ASSEMBLIES))
    check(not editor, f"{folder.name}: no editor assemblies", ", ".join(editor))
    game = folder / "MarvinGodot.dll"
    if game.exists():
        info = pe_info(game)
        check(bool(info and info["managed"]), f"{folder.name}: MarvinGodot.dll is a managed assembly")
        data = game.read_bytes()
        config = "ExportRelease" if b"ExportRelease" in data else "ExportDebug" if b"ExportDebug" in data else "Debug" if b"Debug" in data else "?"
        check(config.startswith("Export"), f"{folder.name}: game assembly configuration", config)
        named = set()
        for dll in (folder / "MarvinGodot.dll", folder / "MarvinCore.dll"):
            if dll.exists(): named |= native_names(dll)
        unexpected = sorted(n for n in named if n not in ALLOWED_NATIVE)
        check(not unexpected, f"{folder.name}: native libraries named by the game are the guarded macOS ones",
              "; ".join(f"{n} ({ALLOWED_NATIVE[n]})" for n in sorted(named) if n in ALLOWED_NATIVE) + (f"; unexpected {unexpected}" if unexpected else ""))

# ---------------------------------------------------------------- Windows
def pe_resources(path):
    """Resource types and the version strings of a PE file."""
    data = Path(path).read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec, = struct.unpack_from("<H", data, pe + 6)
    optsize, = struct.unpack_from("<H", data, pe + 20)
    sec = pe + 24 + optsize
    rsrc = None
    for i in range(nsec):
        name, vsize, va, rawsize, rawptr = struct.unpack_from("<8sIIII", data, sec + i * 40)
        if name.rstrip(b"\0") == b".rsrc": rsrc = (va, rawptr, rawsize)
    if not rsrc: return {}, {}
    va, rawptr, rawsize = rsrc
    def entries(off):
        named, ids = struct.unpack_from("<HH", data, rawptr + off + 12)
        for i in range(named + ids):
            ident, target = struct.unpack_from("<II", data, rawptr + off + 16 + i * 8)
            yield ident, target
    types = {}
    version = {}
    for type_id, t in entries(0):
        count = 0
        for _, n in entries(t & 0x7FFFFFFF):
            for _, l in entries(n & 0x7FFFFFFF):
                rva, size = struct.unpack_from("<II", data, rawptr + (l & 0x7FFFFFFF))
                count += 1
                if type_id == 16:
                    blob = data[rawptr + rva - va: rawptr + rva - va + size].decode("utf-16-le", "replace")
                    for key in ("FileDescription", "ProductName", "FileVersion", "ProductVersion", "CompanyName"):
                        m = re.search(key + r"\x00+([^\x00]*)", blob)
                        if m: version[key] = m.group(1)
        types[type_id] = count
    return types, version

def verify_windows(folder):
    exe = folder / "MarvinSimulator.exe"
    console = folder / "MarvinSimulator.console.exe"
    pck = folder / "MarvinSimulator.pck"
    for f in (exe, pck): check(f.exists(), f"{f.name} exists")
    if not exe.exists(): return
    info = pe_info(exe)
    check(bool(info) and info["machine"] == 0x8664 and info["pe32plus"] and info["subsystem"] == 2, "MarvinSimulator.exe: PE32+ x86-64 GUI", str(info))
    if console.exists():
        ci = pe_info(console)
        check(bool(ci) and ci["machine"] == 0x8664 and ci["subsystem"] == 3, "MarvinSimulator.console.exe: console wrapper for terminal runs", str(ci))
    else:
        check(False, "MarvinSimulator.console.exe exists (preset debug/export_console_wrapper=2)")
    types, version = pe_resources(exe)
    check(types.get(3, 0) > 0 and types.get(14, 0) > 0, "exe icon resources (application/icon, modify_resources)", f"{types.get(3, 0)} icon images")
    check(version.get("ProductName") == "Marvin Simulator", "exe version resource", json.dumps(version))
    d3d = [n for n in ("D3D12Core.dll", "d3d12SDKLayers.dll") if (folder / n).exists() or (folder / "x86_64" / n).exists()]
    print(f"INFO Direct3D 12 Agility SDK: {'exported (' + ', '.join(d3d) + ')' if d3d else 'not shipped; D3D12 uses the Windows system runtime (Vulkan is the fallback)'}")
    check_dotnet(folder / "data_MarvinGodot_windows_x86_64", "win-x64", ".dll", lambda p: (pe_info(p) or {}).get("machine") == 0x8664)
    stray = sorted(p.name for p in folder.rglob("*") if p.suffix in {".dylib", ".so"})
    check(not stray, "no macOS or Linux libraries in the Windows build", ", ".join(stray[:5]))
    check_pack(pck)

# ---------------------------------------------------------------- macOS
CPU = {0x0100000C: "arm64", 0x01000007: "x86_64"}
def macho_archs(path):
    with open(path, "rb") as f:
        head = f.read(8)
        magic, = struct.unpack(">I", head[:4])
        if magic in (0xCAFEBABE, 0xCAFEBABF):
            n, = struct.unpack(">I", head[4:8])
            archs = []
            for _ in range(n):
                cpu, = struct.unpack(">i", f.read(4))
                f.read(16 if magic == 0xCAFEBABE else 28)
                archs.append(CPU.get(cpu & 0xFFFFFFFF, hex(cpu)))
            return archs
        le, = struct.unpack("<I", head[:4])
        if le in (0xFEEDFACF, 0xFEEDFACE):
            cpu, = struct.unpack("<i", head[4:8])
            return [CPU.get(cpu & 0xFFFFFFFF, hex(cpu))]
    return []

def verify_macos(folder):
    apps = sorted(folder.glob("*.app"))
    if not check(len(apps) == 1, "one .app bundle", ", ".join(a.name for a in apps)): return
    app = apps[0]
    contents = app / "Contents"
    plist = plistlib.loads((contents / "Info.plist").read_bytes())
    exe = contents / "MacOS" / plist.get("CFBundleExecutable", "?")
    check(exe.exists(), "Info.plist executable", plist.get("CFBundleExecutable"))
    check(plist.get("CFBundleIdentifier") == "io.entire.marvin.simulator.godot", "bundle identifier", plist.get("CFBundleIdentifier"))
    archs = macho_archs(exe) if exe.exists() else []
    check(sorted(archs) in (["arm64", "x86_64"], ["arm64"]), "executable architectures", ", ".join(archs))
    icon = contents / "Resources" / (plist.get("CFBundleIconFile") or "icon.icns")
    template_icon = None
    for base in (Path.home() / "Library/Application Support/Godot/export_templates",):
        for z in base.glob("*/macos.zip"):
            try:
                with zipfile.ZipFile(z) as zf: template_icon = zf.read("macos_template.app/Contents/Resources/icon.icns")
            except Exception: pass
    check(icon.exists() and (template_icon is None or icon.read_bytes() != template_icon), "app icon is the game's (not Godot's)", icon.name)
    for arch in archs:
        rid = {"arm64": "osx-arm64", "x86_64": "osx-x64"}[arch]
        check_dotnet(contents / "Resources" / f"data_MarvinGodot_macos_{arch}", rid, ".dylib", lambda p, a=arch: macho_archs(p) == [a])
    pcks = sorted((contents / "Resources").glob("*.pck"))
    if check(len(pcks) == 1, "pack in Contents/Resources"): check_pack(pcks[0])
    if sys.platform == "darwin":
        import subprocess
        r = subprocess.run(["codesign", "--verify", "--deep", "--strict", str(app)], capture_output=True, text=True)
        check(r.returncode == 0, "code signature valid (ad-hoc)", r.stderr.strip())
        r = subprocess.run(["codesign", "-d", "--entitlements", ":-", str(app)], capture_output=True, text=True)
        keys = re.findall(r"<key>com\.apple\.security\.([^<]+)</key>", r.stdout)
        check("cs.allow-jit" in keys, ".NET JIT entitlement", ", ".join(keys))

def main():
    if len(sys.argv) != 2: print(__doc__); return 2
    folder = Path(sys.argv[1]).resolve()
    if any(folder.glob("*.app")): verify_macos(folder)
    elif (folder / "MarvinSimulator.exe").exists(): verify_windows(folder)
    else: print(f"FAIL no export found in {folder}"); return 1
    print(f"{'FAILED' if failures else 'OK'}: {folder} ({len(failures)} failed)")
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main())
