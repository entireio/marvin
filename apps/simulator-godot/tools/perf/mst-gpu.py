#!/usr/bin/env python3
"""GPU time per process and per encoder from a Metal System Trace (xctrace).

    mst-gpu.py TRACE.trace [--process NAME] [--frames N] [--json OUT.json] [--top 40]

Reads the trace's metal-gpu-intervals table (exported with `xctrace export`). For each process: GPU busy time as the
union of its top-level encoder intervals on all channels (vertex, fragment and compute overlap on Apple GPUs, so their
plain sum over-counts), and the per-channel sums. For the selected process (default: the busiest one that is not
WindowServer): GPU time per encoder label (run tools/perf/metal-labels.m in the process to name encoders by their
attachments and pipelines; otherwise Instruments' default "Render Command N"), summed over the trace and divided by the
number of frames presented (--frames, or the trace's frame count), i.e. GPU milliseconds per frame per pass.
"""
import argparse
import collections
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

COLUMNS = ['start', 'duration', 'channel', 'frame', 'latency', 'depth', 'label', 'state', 'connection', 'color', 'process',
           'gpu', 'subtitle', 'iosurface', 'bytes', 'cmdbuffer', 'encoder', 'submission']


def rows(xml_path):
    ids = {}
    for _, elem in ET.iterparse(xml_path, events=('end',)):
        if elem.tag != 'row':
            continue
        values = []
        for child in list(elem):
            if 'ref' in child.attrib:
                child = ids.get(child.attrib['ref'], child)
            else:
                for sub in child.iter():
                    if 'id' in sub.attrib:
                        ids[sub.attrib['id']] = sub
            values.append(child)
        yield values
        elem.clear()


def union_length(intervals):
    total, end = 0, None
    for a, b in sorted(intervals):
        if end is None or a > end:
            total += b - a
            end = b
        elif b > end:
            total += b - end
            end = b
    return total


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('trace', type=Path)
    p.add_argument('--process', help='process name substring (default: busiest non-WindowServer process)')
    p.add_argument('--frames', type=float, help='frames presented during the trace (for ms per frame)')
    p.add_argument('--json', type=Path)
    p.add_argument('--top', type=int, default=40)
    a = p.parse_args()
    xml_path = a.trace.with_suffix('.gpu-intervals.xml')
    if not xml_path.exists():
        with open(xml_path, 'w') as out:
            subprocess.run(['xctrace', 'export', '--input', str(a.trace), '--xpath',
                            '/trace-toc/run[@number="1"]/data/table[@schema="metal-gpu-intervals"]'], stdout=out, stderr=subprocess.DEVNULL, check=True)
    per_process = collections.defaultdict(list)
    for v in rows(xml_path):
        if len(v) < 11:
            continue
        depth = v[5].text if v[5].tag != 'sentinel' else '0'
        if depth not in ('0', None):
            continue
        start, dur = int(v[0].text), int(v[1].text)
        channel = v[2].attrib.get('fmt', '')
        frame = v[3].text if v[3].tag != 'sentinel' else None
        label = v[6].attrib.get('fmt', '') if v[6].tag != 'sentinel' else ''
        process = v[10].attrib.get('fmt', '') if v[10].tag != 'sentinel' else ''
        if not process:
            m = re.search(r'\(([^()]*) \((\d+)\)\)\s*$', label)
            process = f'{m.group(1)} ({m.group(2)})' if m else 'n/a'
        per_process[process].append((start, dur, channel, frame, label))
    report = {'trace': str(a.trace), 'processes': {}}
    span_all = None
    for proc, items in per_process.items():
        t0 = min(s for s, *_ in items)
        t1 = max(s + d for s, d, *_ in items)
        span = (t1 - t0) / 1e9
        busy = union_length([(s, s + d) for s, d, *_ in items]) / 1e6
        channels = collections.Counter()
        for s, d, ch, *_ in items:
            channels[ch] += d / 1e6
        report['processes'][proc] = {'spanSeconds': span, 'busyMSPerSecond': busy / span if span else 0,
                                     'channelMSPerSecond': {k: v / span for k, v in channels.items()}, 'intervals': len(items)}
    ranked = sorted(report['processes'].items(), key=lambda kv: -kv[1]['busyMSPerSecond'])
    for proc, r in ranked:
        print(f"{proc:40s} busy {r['busyMSPerSecond']:6.1f} ms/s over {r['spanSeconds']:.2f} s  " +
              ', '.join(f"{k} {v:.0f}" for k, v in sorted(r['channelMSPerSecond'].items())))
    target = None
    for proc, _ in ranked:
        if (a.process and a.process in proc) or (not a.process and 'WindowServer' not in proc and proc != 'n/a'):
            target = proc
            break
    if not target:
        sys.exit('no matching process')
    items = per_process[target]
    frames = a.frames or len({f for *_, f, _ in items if f is not None}) or 1
    span = report['processes'][target]['spanSeconds']
    by_label = collections.defaultdict(lambda: collections.Counter())
    count = collections.Counter()
    busy_by_label = collections.defaultdict(list)
    draws = collections.Counter()
    for s, d, ch, f, label in items:
        # "<label>  (Process (pid)) 0x<encoder id>": drop the process and encoder id, the command buffer number and the
        # per-encoder draw count (kept as draws per encoder).
        clean = re.sub(r'\s*\([^()]*\(\d+\)\)\s*(0x[0-9a-fA-F]+)?\s*$', '', label)
        clean = re.sub(r'^Command Buffer \d+:\s*', '', clean)
        m = re.search(r'\s*\|\s*(\d+) draws$', clean)
        if m:
            clean = clean[:m.start()]
            if ch != 'Vertex':
                draws[clean] += int(m.group(1))
        # Instruments joins encoders that ran as one GPU interval with " & "; a compute label's dispatch count and
        # grid change while one encoder accumulates dispatches: keep the encoder kind, ordinal and pipelines.
        parts = []
        for part in clean.split(' & '):
            part = re.sub(r' x\d+ \d+x\d+$', '', part.strip())
            if part not in parts:
                parts.append(part)
        clean = ' & '.join(parts)
        by_label[clean][ch] += d / 1e6
        if ch != 'Vertex':
            count[clean] += 1
        busy_by_label[clean].append((s, s + d))
    rows_out = []
    for label, chans in by_label.items():
        busy = union_length(busy_by_label[label]) / 1e6
        rows_out.append({'label': label, 'busyMSPerFrame': busy / frames, 'channelMSPerFrame': {k: v / frames for k, v in chans.items()},
                         'encodersPerFrame': count[label] / frames, 'drawsPerEncoder': draws[label] / count[label] if count[label] else 0})
    rows_out.sort(key=lambda r: -r['busyMSPerFrame'])
    total = report['processes'][target]['busyMSPerSecond'] * span / frames
    print(f"\n{target}: {frames:.0f} frames, GPU busy {total:.2f} ms per frame (union of all encoders)")
    print(f"{'busy ms/frame':>13s} {'enc/frame':>9s} {'draws/enc':>9s}  channels (ms/frame)    label")
    for r in rows_out[:a.top]:
        chans = ' '.join(f"{k[0]}{v:.2f}" for k, v in sorted(r['channelMSPerFrame'].items()))
        print(f"{r['busyMSPerFrame']:13.3f} {r['encodersPerFrame']:9.2f} {r['drawsPerEncoder']:9.0f}  {chans:22s} {r['label']}")
    report['target'] = target
    report['frames'] = frames
    report['busyMSPerFrame'] = total
    report['encoders'] = rows_out
    if a.json:
        a.json.write_text(json.dumps(report, indent=1) + '\n')


if __name__ == '__main__':
    main()
