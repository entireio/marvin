#!/usr/bin/env python3
"""Summarize the legacy HUD fallback when metalperftrace is unavailable.

Uses the final N seconds, excluding initialization. Logs can repeat samples;
these are distributions of logged samples, not a count of unique presented frames.
Never substitutes callback timing for GPU timing or claims this is a GPU capture.
"""
import argparse
import datetime
import json
import statistics
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('log', type=Path)
parser.add_argument('output', type=Path)
parser.add_argument('--tail-seconds', type=float, default=35)
args = parser.parse_args()
rows = []
for line in args.log.read_text().splitlines():
    if 'metal-HUD:' not in line:
        continue
    try:
        stamp = datetime.datetime.fromisoformat(line[:23])
        values = [float(v) for v in line.split('metal-HUD:', 1)[1].split(',')]
    except ValueError:
        continue
    if len(values) < 5 or (len(values) - 3) % 2:
        raise ValueError('Unrecognized HUD payload; inspect the current OS format')
    rows.append((stamp, values))
if not rows:
    raise ValueError('No unredacted HUD samples; no result can be inferred')
end = rows[-1][0]
rows = [(time, v) for time, v in rows if (end-time).total_seconds() <= args.tail_seconds]
intervals = [n for _, v in rows for n in v[3::2]]
gpu = [n for _, v in rows for n in v[4::2]]
def distribution(values):
    values = sorted(values)
    return {'meanMS': statistics.mean(values), 'p50MS': values[len(values)//2],
            'p95MS': values[int((len(values)-1)*.95)],
            'p99MS': values[int((len(values)-1)*.99)], 'maxMS': values[-1]}
result = {
    'source': str(args.log), 'method': 'Apple Metal HUD legacy console logging',
    'limitation': 'Logged samples may repeat; not unique-frame accounting. HUD overhead is present. GPU time is HUD-reported, not an encoder trace.',
    'windowSeconds': (end-rows[0][0]).total_seconds(),
    'loggedSamples': len(gpu), 'gpu': distribution(gpu),
    'presentationInterval': distribution(intervals),
    'processMemoryPeakMB': max(v[1] for _, v in rows),
    'metalAllocatedPeakMB': max(v[2] for _, v in rows),
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, indent=2)+'\n')
print(json.dumps(result, indent=2))
