#!/usr/bin/env python3
"""Table of run-benchmark.py results: one row per (label, mode, size), mean over repeated runs (min-max in brackets).

    summarize-runs.py RUN_DIR... [--label-from-name]   (directories with run.json)

The label is the run's app, or with --label-from-name the directory name up to the first '-<mode>' part
(e.g. 'release-godot-city-roam-960x540-r1' -> 'release-godot').
"""
import argparse
import collections
import json
import statistics
from pathlib import Path


def fmt(values, digits=1):
    values = [v for v in values if v is not None]
    if not values:
        return '-'
    m = statistics.mean(values)
    if len(values) == 1 or max(values) - min(values) < 10 ** -digits:
        return f'{m:.{digits}f}'
    return f'{m:.{digits}f} ({min(values):.{digits}f}-{max(values):.{digits}f})'


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('runs', nargs='+', type=Path)
    p.add_argument('--label-from-name', action='store_true')
    a = p.parse_args()
    groups = collections.defaultdict(list)
    for d in a.runs:
        f = d / 'run.json'
        if not f.exists():
            continue
        r = json.loads(f.read_text())
        if 'benchmark' not in r:
            continue
        mode = r['run']['mode']
        label = r['run']['app']
        if a.label_from_name:
            name = d.name
            label = name.split('-' + mode)[0] if '-' + mode in name else name
        b = r['benchmark']
        groups[(label, mode, f"{b['drawableWidth']:.0f}x{b['drawableHeight']:.0f}")].append(r)
    print('| build | mode | drawable | runs | FPS | p50 ms | p95 ms | p99 ms | frames > 25 ms | GPU ms/frame | GPU busy ms/s | CPU tick p95 ms | render span p50 ms | RSS MB |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for (label, mode, size), rs in sorted(groups.items(), key=lambda kv: (kv[0][1], kv[0][2], kv[0][0])):
        b = [r['benchmark'] for r in rs]
        m = [r.get('metal', {}) for r in rs]
        t = [r.get('timeline', {}) for r in rs]
        print(f"| {label} | {mode} | {size} | {len(rs)} | {fmt([x['meanFPS'] for x in b], 2)} | {fmt([x['p50MS'] for x in b], 2)} | "
              f"{fmt([x['p95MS'] for x in b], 2)} | {fmt([x['p99MS'] for x in b], 2)} | {fmt([x['over25MS'] for x in b], 0)} | "
              f"{fmt([x.get('gpuMSPerFrame') for x in m], 2)} | {fmt([x.get('gpuBusyMSPerSecond') for x in m], 0)} | "
              f"{fmt([x['cpuUpdateP95MS'] for x in b], 2)} | {fmt([(x.get('renderCallbackSpanMS') or {}).get('p50') for x in t], 2)} | "
              f"{fmt([(r.get('process') or {}).get('rssMB', {}).get('mean') for r in rs], 0)} |")


if __name__ == '__main__':
    main()
