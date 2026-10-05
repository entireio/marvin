#!/usr/bin/env python3
"""GPU busy ms per frame by pass, for several run-benchmark.py runs side by side.

    gpu-passes.py RUN_DIR...

Reads each run's mst-gpu.json (run-benchmark.py --labels --trace) and sums the encoders into Godot's passes by their
metal-labels.m names: shadow atlas clear and maps, depth prepass, the MSAA resolves, SSAO (in one compute encoder with
the depth/normal resolve), opaque and transparent passes (same attachments, told apart by draw count), glow, tonemap and
2D, window blit, blits (uploads). "TOTAL busy" is the union of all encoders, the most robust number; "enc/frame" below
1 for a pass means some of its encoders were joined to another label by Instruments in that trace (compare totals then).
Rows CONTENTION = 1 mark runs whose trace shows other GPU work (run.json traceGPU.contention).
"""
import json, re, sys, os


def classify(label, draws):
    if 'D16 8192x8192 clear' in label:
        return 'shadow atlas clear'
    if 'D16 8192x8192 load' in label:
        return 'shadow maps'
    if re.search(r'RGBA16F \d+x\d+x2 \| D D32FS8 \S+ load', label):
        return 'opaque' if draws > 60 else 'transparent'
    if re.search(r'RGBA8 \d+x\d+x2 \| D D32FS8 \S+ clear', label):
        return 'depth prepass'
    if 'ResolveShaderRD:0' in label:
        return 'resolve gi + ssao'
    if 'CopyShaderRD' in label:
        return 'final depth resolve + glow'
    if label.startswith('C#') and 'ResolveShaderRD:2' in label:
        return 'mid depth resolve'
    if 'Resolve Image' in label:
        return 'color resolve'
    if label.startswith('B#'):
        return 'blits/uploads'
    if 'BGRA8' in label:
        return 'window blit'
    if re.search(r'^R#\d+ RGBA8 \d+x\d+$', label):
        return 'tonemap/2d'
    if 'GPU Execution' in label:
        return 'other'
    return 'other: ' + label[:40]


def load(run):
    d = json.load(open(os.path.join(run, 'mst-gpu.json')))
    out = {'TOTAL busy': d['busyMSPerFrame']}
    for e in d['encoders']:
        k = classify(e['label'], e.get('drawsPerEncoder', 0))
        out[k] = out.get(k, 0) + e['busyMSPerFrame']
        if k in ('opaque', 'transparent'):
            out[k + ' enc/frame'] = out.get(k + ' enc/frame', 0) + e['encodersPerFrame']
    try:
        r = json.load(open(os.path.join(run, 'run.json')))
        out['FPS'] = r['benchmark']['meanFPS']
        tg = r.get('traceGPU', {})
        if tg.get('contention'):
            out['CONTENTION'] = 1
    except Exception:
        pass
    return out


runs = sys.argv[1:]
data = [load(r) for r in runs]
keys = ['TOTAL busy', 'FPS', 'opaque', 'opaque enc/frame', 'transparent', 'transparent enc/frame', 'shadow maps', 'shadow atlas clear', 'depth prepass', 'resolve gi + ssao',
        'final depth resolve + glow', 'mid depth resolve', 'color resolve', 'tonemap/2d', 'window blit', 'blits/uploads']
keys += sorted({k for d in data for k in d} - set(keys))
names = [os.path.basename(r.rstrip('/')) for r in runs]
print(f"{'pass':28s}" + ''.join(f'{n[:14]:>15s}' for n in names))
for k in keys:
    print(f'{k[:28]:28s}' + ''.join(f"{d.get(k, 0):15.3f}" for d in data))
