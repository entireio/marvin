#!/usr/bin/env python3
"""Compare the synced game assets (assets/, tools/sync-assets.py) with the ones a Mac builds.

    tools/ci/assets-report.py --out OUT                  # CI: assets/ against tools/ci/reference/assets-sha256.json
    tools/ci/assets-report.py --update-reference         # on a Mac after build-app.sh and sync: store the manifest

The generated assets (Marvin's geometry.bin from the STEP master, R2-D2's mesh and textures, BB-8's and WALL-E's meshes
and textures) come from the repository's Python exporters (scripts/prepare-simulator-assets.py); everything else is
copied from apps/simulator-macos/Resources. The manifest holds the SHA-256 of every file tools/sync-assets.py copies
(not the .import files, which Godot rewrites). Differences are reported (a finding), never a failure: the game reads
what is there. Appends a Markdown section to the job summary and writes OUT/assets.json.
"""
import argparse, hashlib, sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cilib import REFERENCE, ROOT, append_summary, md_escape, write_json

MANIFEST = REFERENCE / "assets-sha256.json"
SKIP_SUFFIX = {".zip", ".glb", ".hdr", ".rtf", ".icns", ".import"}   # as tools/sync-assets.py (plus Godot's .import)
FOLDERS = ["Marvin", "R2D2", "BB8", "WallE", "City", "Dirt", "Audio", "Icons"]
GENERATED = ("Marvin/", "R2D2/mesh.json", "R2D2/Textures/", "BB8/Generated/", "WallE/Generated/")


def manifest(base):
    result = {}
    for folder in FOLDERS:
        for path in sorted((base / folder).rglob("*")):
            if path.is_file() and path.suffix.lower() not in SKIP_SUFFIX:
                result[path.relative_to(base).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", default=str(ROOT / "assets"))
    parser.add_argument("--out")
    parser.add_argument("--update-reference", action="store_true")
    args = parser.parse_args()
    here = manifest(Path(args.assets))
    if args.update_reference:
        write_json(MANIFEST, here)
        print(f"wrote {len(here)} hashes to {MANIFEST}")
        return 0
    import json
    mac = json.loads(MANIFEST.read_text())
    differ = sorted(k for k in mac if k in here and here[k] != mac[k])
    missing = sorted(k for k in mac if k not in here)
    extra = sorted(k for k in here if k not in mac)
    same = len(mac) - len(differ) - len(missing)
    generated_differ = [k for k in differ if k.startswith(GENERATED)]
    md = ["## Game assets against a Mac's", "",
          f"`tools/sync-assets.py` output (generated meshes from `scripts/prepare-simulator-assets.py` on this runner) "
          f"against the SHA-256 manifest of a Mac's (`tools/ci/reference/assets-sha256.json`): **{same} of {len(mac)} files "
          f"identical**" + (f", {len(differ)} differ ({len(generated_differ)} of them generated)" if differ else "")
          + (f", {len(missing)} missing" if missing else "") + (f", {len(extra)} not on the Mac" if extra else "") + "."]
    if differ or missing or extra:
        md += ["", "| File | State |", "|---|---|"]
        md += [f"| `{md_escape(k)}` | differs |" for k in differ[:40]]
        md += [f"| `{md_escape(k)}` | missing here |" for k in missing[:20]]
        md += [f"| `{md_escape(k)}` | only here |" for k in extra[:20]]
    if args.out:
        write_json(Path(args.out) / "assets.json", {"identical": same, "total": len(mac), "differ": differ, "missing": missing, "extra": extra})
    append_summary("\n".join(md) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
