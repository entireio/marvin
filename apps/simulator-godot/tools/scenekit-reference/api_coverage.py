#!/usr/bin/env python3
"""SceneKit/AppKit API coverage of the Godot facade.

Collects every member name declared in the macOS SDK headers of the SceneKit
classes the game uses (plus NSColor/NSImage/NSBitmapImageRep/NSBezierPath and
Metal types), intersects them with the `.member` tokens used in
apps/simulator-macos/Sources/MarvinSimulator, and reports which of those are
defined in apps/simulator-godot/scripts/SceneKit (by identifier).

  python3 apps/simulator-godot/tools/scenekit-reference/api_coverage.py
"""
import re, subprocess, sys
from pathlib import Path

root = Path(__file__).resolve().parents[2]
swift_dir = root.parent / "simulator-macos/Sources/MarvinSimulator"
facade_dir = root / "scripts/SceneKit"
sdk = subprocess.run(["xcrun", "--show-sdk-path"], capture_output=True, text=True).stdout.strip()
frameworks = Path(sdk) / "System/Library/Frameworks"
headers = {
    "SceneKit": ["SCNNode.h", "SCNGeometry.h", "SCNMaterial.h", "SCNMaterialProperty.h", "SCNLight.h", "SCNCamera.h",
                 "SCNScene.h", "SCNSceneRenderer.h", "SCNRenderer.h", "SCNView.h", "SCNLevelOfDetail.h", "SCNParametricGeometry.h",
                 "SCNConstraint.h", "SCNTransaction.h", "SCNShadable.h", "SCNBoundingVolume.h", "SCNHitTest.h", "SceneKitTypes.h"],
    "AppKit": ["NSColor.h", "NSImage.h", "NSBitmapImageRep.h", "NSBezierPath.h", "NSGraphicsContext.h", "NSFont.h"],
    "Metal": ["MTLTexture.h", "MTLBuffer.h", "MTLDevice.h"],
}
declared = set()
for fw, names in headers.items():
    for name in names:
        path = frameworks / f"{fw}.framework/Headers/{name}"
        if not path.exists():
            continue
        text = path.read_text(errors="ignore")
        # @property (...) Type *name;  /  - (Type)name...  /  + (Type)name...
        declared |= set(re.findall(r"@property\b[^;]*?\b(\w+)\s*(?:API_|NS_|SCN_|MTL_|;|__)", text))
        declared |= set(re.findall(r"^[-+]\s*\([^)]*\)\s*(\w+)", text, re.M))
        declared |= set(re.findall(r"(?:SCN|NS|MTL)\w*(?:Make|Mult|Invert|Translate|Scale|Rotate|EqualTo|IsIdentity|To|From)\w*", text))
        declared |= set(re.findall(r"^\s*(SCN\w+|NS\w+|MTL\w+)\s*(?:API_|NS_|=|,)", text, re.M))
swift_tokens = {}
for f in sorted(swift_dir.glob("*.swift")):
    if f.name in {"RendererStudy.swift", "BenchmarkGPUCapture.swift"}:
        continue
    for tok in re.findall(r"\.(\w+)", f.read_text()):
        swift_tokens.setdefault(tok, set()).add(f.name)
    for tok in re.findall(r"\b(SCN\w+|MTL\w+)\b", f.read_text()):
        swift_tokens.setdefault(tok, set()).add(f.name)
facade_text = "\n".join(p.read_text() for p in facade_dir.glob("*.cs"))
facade_ids = set(re.findall(r"\b(\w+)\b", facade_text))
# Swift-only spellings that differ from Objective-C selectors.
aliases = {"position": "position", "simdPosition": "simdPosition", "addChildNode": "addChildNode"}
used = sorted(t for t in swift_tokens if t in declared or t.startswith(("SCN", "MTL")))
missing = [t for t in used if t not in facade_ids]
print(f"SceneKit/AppKit/Metal members used by the app: {len(used)}; defined in the facade: {len(used) - len(missing)}")
if missing:
    print("Not defined in the facade (name match):")
    for t in missing:
        print(f"  {t:40} {', '.join(sorted(swift_tokens[t]))[:110]}")
