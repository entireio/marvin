#!/usr/bin/env python3
"""Inspect Instruments Game Performance Overview exports; not an acceptance gate.

Export metal-perf-overview-layer-per-frame-interval-metric with xctrace first.
Keep process/layer identities separate. Cadence uses adjacent On Display starts;
Apple's exported interval ends have a calibrated 41–42 ns quantization gap.
"""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def read_intervals(path):
    root = ET.parse(path).getroot()
    ids = {e.attrib['id']: e for e in root.iter() if 'id' in e.attrib}
    def resolve(e):
        return ids[e.attrib['ref']] if 'ref' in e.attrib else e
    groups = defaultdict(list)
    nodes = root.findall('node')
    if len(nodes) != 1:
        raise ValueError('Export exactly one run and one interval table')
    for node in nodes:
        schema = node.find('schema')
        if schema is None or schema.get('name') != 'metal-perf-overview-layer-per-frame-interval-metric':
            raise ValueError('Expected per-frame interval schema')
        columns = [c.findtext('mnemonic') for c in schema.findall('col')]
        for row in node.findall('row'):
            if len(row) != len(columns):
                raise ValueError('Incomplete interval row')
            values = dict(zip(columns, map(resolve, row)))
            process = values['process']
            pid = resolve(process.find('pid')).text
            frame = re.search(r'\(Frame (\d+)\)', values['label'].text or '')
            if frame is None:
                raise ValueError('Missing layer-local frame number')
            item = {k: int(values[k].text) for k in ('start', 'end', 'duration')}
            item['frame'] = int(frame[1])
            if item['duration'] < 0 or item['end']-item['start'] != item['duration']:
                raise ValueError('Invalid interval bounds')
            key = (pid, values['process-name'].text, values['layer-id'].text, values['name'].text)
            groups[key].append(item)
    if not groups:
        raise ValueError('No interval rows')
    return groups


def distribution(ns):
    ordered = sorted(ns)
    if not ordered:
        return None
    return {'count': len(ns), 'meanMS': sum(ns)/len(ns)/1e6,
            **{f'p{p}MS': ordered[int((len(ns)-1)*p/100)]/1e6 for p in (50,95,99)},
            'maxMS': ordered[-1]/1e6, 'over25MS': sum(v>25_000_000 for v in ns)}


def summarize(groups):
    result = []
    for (pid, process, layer, metric), rows in sorted(groups.items()):
        rows.sort(key=lambda r:r['start'])
        frames = [r['frame'] for r in rows]
        gaps = [b['start']-a['end'] for a,b in zip(rows,rows[1:])]
        display = metric.endswith('.On Display')
        intervals = [b['start']-a['start'] for a,b in zip(rows,rows[1:])] if display else [r['duration'] for r in rows]
        result.append({'pid': int(pid), 'process': process, 'layerID': layer, 'metric': metric,
                       'firstStartSeconds': rows[0]['start']/1e9, 'lastEndSeconds': rows[-1]['end']/1e9,
                       'cadenceCoverageEndSeconds': rows[-1]['start']/1e9 if display else None,
                       'terminalDisplayDurationMS': rows[-1]['duration']/1e6 if display else None,
                       'terminalDisplayOver25MS': rows[-1]['duration']>25_000_000 if display else None,
                       'frameIDsContiguous': frames == list(range(frames[0],frames[0]+len(frames))),
                       'maximumAbsoluteEndToNextStartNS': max(map(abs,gaps),default=0) if display else None,
                       'distribution': distribution(intervals),
                       'traceMinuteDistributions': {str(minute): distribution([
                           intervals[i] for i in range(len(intervals)) if rows[i]['start']//60_000_000_000==minute
                       ]) for minute in sorted({r['start']//60_000_000_000 for r in rows})}})
    return {'overallComplete': False, 'metrics': result,
            'limitations': 'Diagnostic only. No benchmark clock alignment, SCNView layer attribution, lost-event validation, instrument-overhead correction or sustained acceptance. GPU Begin to End is an envelope, not exclusive encoder busy time.'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('intervals',type=Path)
    parser.add_argument('--output',type=Path)
    args = parser.parse_args()
    text = json.dumps(summarize(read_intervals(args.intervals)),indent=2)+'\n'
    if args.output:
        args.output.write_text(text)
    print(text,end='')
