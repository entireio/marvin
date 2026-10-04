#!/usr/bin/env python3
"""Compares TextCalibration.swift (CoreText) with `tools/godot -- --text-calibration` (facade) output.

    python3 text_calibration.py MAC_DIR GODOT_DIR [--fit]

Per font: ink ratio Godot/CoreText for every case (plain/label x greys), the vertical ink-profile error, the
CoreText stem gain equivalent to each case (from Godot's fixed-gain rows; --fit searches the FontSmoothing
constants), glyph-position offsets (positions-*.png) and string sizes (widths.json). Needs numpy and Pillow.
"""
import json, math, os, re, sys, itertools
import numpy as np
from PIL import Image

GREYS = ['0', '0.25', '0.5', '0.75', '1']
GAINS = [0, 0.25, 0.5, 0.75, 1.0, 1.5]
CASES = ['raw-0', 'raw-1'] + ['plain-' + g for g in GREYS] + ['label-' + g for g in GREYS]


def size_of(name): return float(re.findall(r'\d+', name)[0])
def lin(v): return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def rows(path, name, godot):
    a = np.asarray(Image.open(path).convert('RGB')).astype(float)[..., 1] / 255
    row_h = math.ceil(size_of(name) * 1.5) + 12
    out = {}
    for i, c in enumerate(CASES + (['gain-%g' % g for g in GAINS] if godot else [])):
        g = 0.0 if c.startswith('gain') else float(c.split('-')[1])
        A = a[i * row_h + 1:(i + 1) * row_h - 1, 1:-1]
        b = A[0, 0]
        cov = (A - b) / (g - b)
        out[c] = (float(cov.sum()), cov.sum(1))
    return out


def main():
    mac, gd = sys.argv[1], sys.argv[2]
    fonts = sorted(f[:-4] for f in os.listdir(mac) if f.endswith('.png') and not f.startswith('positions'))
    data, ratios, profiles = [], [], []
    print('font     ink ratio Godot/CoreText: plain greys 0..1 | label greys 0..1 | profile error')
    for n in fonts:
        m = rows(os.path.join(mac, n + '.png'), n, False); g = rows(os.path.join(gd, n + '.png'), n, True)
        curve = [g['gain-%g' % x][0] for x in GAINS]
        def eq(ink):
            for i in range(len(GAINS) - 1):
                if ink <= curve[i + 1] or i == len(GAINS) - 2:
                    return GAINS[i] + (ink - curve[i]) * (GAINS[i + 1] - GAINS[i]) / (curve[i + 1] - curve[i])
        slope = (curve[2] - curve[0]) / 0.5
        line, perr = f'{n:8s}', []
        for kind in ['plain', 'label']:
            for x in GREYS:
                c = f'{kind}-{x}'
                q = g[c][0] / m[c][0]; ratios.append(abs(q - 1)); line += f' {q:.3f}'
                perr.append(np.abs(g[c][1] - m[c][1]).sum() / m[c][1].sum())
                data.append((size_of(n), lin(float(x)), kind == 'label', eq(m[c][0]), slope))
            line += ' |'
        profiles.append(np.mean(perr))
        print(line + f' {np.mean(perr):.3f}')
    print(f'mean |ink ratio - 1| {np.mean(ratios):.4f} (max {np.max(ratios):.4f}); mean profile error {np.mean(profiles):.4f}')

    if '--fit' in sys.argv:
        def model(p, s, L, cell):
            black, white, cap, cellg, floor, power = p
            f = min(1, max(0, (L - floor) / (1 - floor))) ** power
            return min(cap, s * (black + (white - black) * f)) + (cellg if cell else 0)
        best = None
        for p in itertools.product(np.arange(0.011, 0.0161, 0.0005), np.arange(0.030, 0.0401, 0.001), [0.68, 0.69, 0.70, 0.71, 0.72, 0.73],
                                   [0.29, 0.30, 0.31, 0.32, 0.33], [0.0, 0.02, 0.04, 0.06], [0.6, 0.7, 0.8, 0.9, 1.0]):
            e = sum(((model(p, s, L, c) - v) * sl) ** 2 for s, L, c, v, sl in data)
            if best is None or e < best[0]: best = (e, p)
        print('FontSmoothing fit (Black, White, Cap, Cell, LuminanceFloor, LuminancePower):', tuple(round(float(v), 4) for v in best[1]))

    def centroids(path):
        a = np.asarray(Image.open(path).convert('RGB')).astype(float)[..., 1] / 255
        return np.array([(a[10 + k * 70:75 + k * 70].sum(0) * np.arange(a.shape[1])).sum() / a[10 + k * 70:75 + k * 70].sum() for k in range(20)])
    for f in sorted(os.listdir(mac)):
        if f.startswith('positions') and os.path.exists(os.path.join(gd, f)):
            d = centroids(os.path.join(gd, f)) - centroids(os.path.join(mac, f))
            print(f'{f:28s} glyph x offset Godot - CoreText: mean {d.mean():+.3f} px, max |{np.abs(d).max():.3f}|')
    wm, wg = os.path.join(mac, 'widths.json'), os.path.join(gd, 'widths.json')
    if os.path.exists(wm) and os.path.exists(wg):
        m, g = json.load(open(wm)), json.load(open(wg))
        dw = [g[k][0] - m[k][0] for k in m if k in g]; dh = [g[k][1] - m[k][1] for k in m if k in g]
        print(f'string sizes: width max |{max(map(abs, dw)):.3f}| px, height mismatches {sum(1 for v in dh if v)} of {len(dh)}')


if __name__ == '__main__':
    main()
