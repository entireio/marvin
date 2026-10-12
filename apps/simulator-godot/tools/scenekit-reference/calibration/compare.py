#!/usr/bin/env python3
"""Compare SceneKit (sk/) and Godot facade (gd/) calibration renders.

compare.py SK_DIR GD_DIR OUT_DIR [names...]
Writes OUT_DIR/compare-NAME.png (SceneKit over Godot over |diff| x4) and
OUT_DIR/ab-NAME.png (half-size side by side), prints image statistics and probe values.
"""
import json, sys, os
import numpy as np
from PIL import Image

sk_dir, gd_dir, out_dir = sys.argv[1:4]
names = sys.argv[4:] or ["race_midday_chase", "race_midday_overview", "race_evening_chase", "sandbox_orbit", "sandbox_overview", "menu_portrait"]
os.makedirs(out_dir, exist_ok=True)
probes = json.load(open(os.path.join(sk_dir, "probes.json")))

def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float64)
def lin(v):
    v = v / 255.0
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)
def luma(img):
    l = lin(img)
    return 0.2126 * l[..., 0] + 0.7152 * l[..., 1] + 0.0722 * l[..., 2]

summary = {}
for name in names:
    a_path, b_path = os.path.join(sk_dir, name + ".png"), os.path.join(gd_dir, name + ".png")
    if not (os.path.exists(a_path) and os.path.exists(b_path)):
        print(f"{name}: missing"); continue
    a, b = load(a_path), load(b_path)
    d = np.abs(a - b)
    h, w, _ = a.shape
    stack = np.concatenate([a, b, np.clip(d * 4, 0, 255)], axis=0).astype(np.uint8)
    Image.fromarray(stack).save(os.path.join(out_dir, f"compare-{name}.png"))
    ab = np.concatenate([a, b], axis=1).astype(np.uint8)
    Image.fromarray(ab).resize((w, h // 2)).save(os.path.join(out_dir, f"ab-{name}.png"))
    ma, mb = a.reshape(-1, 3).mean(0), b.reshape(-1, 3).mean(0)
    la, lb = luma(a), luma(b)
    print(f"\n=== {name}: mean |d| {d.mean():.2f}/255, mean sRGB SK {ma.round(1)} GD {mb.round(1)}, mean luma SK {la.mean():.4f} GD {lb.mean():.4f} (ratio {lb.mean()/max(la.mean(),1e-6):.3f})")
    # 4x4 grid of luma ratios
    rows = []
    for gy in range(4):
        row = []
        for gx in range(4):
            sa = la[gy*h//4:(gy+1)*h//4, gx*w//4:(gx+1)*w//4].mean(); sb = lb[gy*h//4:(gy+1)*h//4, gx*w//4:(gx+1)*w//4].mean()
            row.append(f"{sb/max(sa,1e-6):5.2f}")
        rows.append(" ".join(row))
    print("  luma ratio GD/SK by 4x4 grid:\n    " + "\n    ".join(rows))
    summary[name] = {"mean_abs": d.mean(), "luma_ratio": lb.mean() / max(la.mean(), 1e-6)}
    for p in probes.get(name, []):
        x, y = int(round(p["x"])), int(round(p["y"]))
        if not (2 <= x < w - 2 and 2 <= y < h - 2): continue
        pa = lin(a[y-2:y+3, x-2:x+3]).reshape(-1, 3).mean(0); pb = lin(b[y-2:y+3, x-2:x+3]).reshape(-1, 3).mean(0)
        ratio = (pb.sum() + 1e-4) / (pa.sum() + 1e-4)
        flag = "  <-" if abs(ratio - 1) > 0.15 else ""
        print(f"  {p['name']:<16} ({x:4d},{y:3d}) SK {pa.round(3)} GD {pb.round(3)} ratio {ratio:5.2f}{flag}")
json.dump(summary, open(os.path.join(out_dir, "summary.json"), "w"), indent=1)
