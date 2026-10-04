#!/usr/bin/env python3
"""Copy the macOS simulator's runtime assets into assets/ (gitignored).

The macOS game is the reference and is never modified. Generated assets
(Marvin geometry.bin, BB8/WallE Generated/) are gitignored there, so by default
they are read from the main checkout; override with MARVIN_MAC_RESOURCES.
Run apps/simulator-macos/build-app.sh first if they are missing.
"""
import os, shutil, subprocess, sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
def main_checkout():
    common = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
                            cwd=root, capture_output=True, text=True).stdout.strip()
    return Path(common).parent if common else root.parent.parent
src = Path(os.environ.get("MARVIN_MAC_RESOURCES", main_checkout() / "apps/simulator-macos/Resources"))
dst = root / "assets"
SKIP_SUFFIX = {".zip", ".glb", ".hdr", ".rtf", ".icns"}
FOLDERS = ["Marvin", "R2D2", "BB8", "WallE", "City", "Dirt", "Audio", "Icons"]
TEXTURE_IMPORT = """[remap]

importer="texture"
type="CompressedTexture2D"

[params]

compress/mode=0
mipmaps/generate=true
"""

if not (src / "Marvin/geometry.bin").exists():
    sys.exit(f"missing {src}/Marvin/geometry.bin - run apps/simulator-macos/build-app.sh in the main checkout")
copied = 0
for folder in FOLDERS:
    for path in (src / folder).rglob("*"):
        if path.is_dir() or path.suffix.lower() in SKIP_SUFFIX:
            continue
        target = dst / path.relative_to(src)
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists() or target.stat().st_mtime < path.stat().st_mtime or target.stat().st_size != path.stat().st_size:
            shutil.copy2(path, target)
            copied += 1
        if path.suffix.lower() in {".jpg", ".png"}:
            imp = target.with_name(target.name + ".import")
            if not imp.exists():
                imp.write_text(TEXTURE_IMPORT)
print(f"assets: {copied} files updated from {src}")
