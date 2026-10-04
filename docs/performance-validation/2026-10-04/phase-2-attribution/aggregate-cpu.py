#!/usr/bin/env python3
"""Recompute Phase 1 statistical CPU weights across every drawing worker."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def table(path):
    root = ET.parse(path).getroot()
    ids = {n.get('id'): n for n in root.iter() if n.get('id')}

    def resolve(n):
        while n.get('ref'):
            n = ids[n.get('ref')]
        return n

    rows = []
    for node in root.findall('node'):
        names = [c.findtext('mnemonic') for c in node.findall('schema/col')]
        rows.extend(dict(zip(names, map(resolve, row))) for row in node.findall('row'))
    return rows, resolve


def analyze(raw, name):
    inputs = [raw / f'metal-{name}-{s}.xml' for s in
              ('time-profile', 'metal-application-encoders-list')]
    toc = raw / f'metal-{name}-toc.xml'
    inputs.append(toc)
    duration = float(ET.parse(toc).findtext('.//summary/duration'))
    rows, resolve = table(inputs[0])
    target_pid = int(ET.parse(toc).find('.//target/process').get('pid'))
    samples = []
    drawing_threads = set()
    # Select workers from observed SceneKit draw stacks, without picking one tid.
    for row in rows:
        process = row['process']
        if int(resolve(process.find('pid')).text) != target_pid:
            continue
        thread = row['thread']
        tid = int(resolve(thread.find('tid')).text)
        label = thread.get('fmt', '')
        frames = [resolve(n).get('name', '') for n in row['stack'].iter('frame')]
        drawing = any('drawRenderElement:withPass:' in f for f in frames)
        if drawing and not label.startswith('Main Thread'):
            drawing_threads.add(tid)
        samples.append((float(row['time'].text) / 1e9, tid, label,
                        float(row['weight'].text) / 1e9, frames, drawing))
    encoders, encoder_resolve = table(inputs[1])
    frame_ids = {int(row['frame-number'].text) for row in encoders
                 if int(encoder_resolve(row['process'].find('pid')).text) == target_pid}
    by_thread = {}
    for tid in sorted(drawing_threads):
        selected = [s for s in samples if s[1] == tid]
        by_thread[str(tid)] = {
            'label': selected[0][2],
            'allSampleWeightSeconds': sum(s[3] for s in selected),
            'drawInclusiveSeconds': sum(s[3] for s in selected if s[5]),
            'executeDrawInclusiveSeconds': sum(s[3] for s in selected
                                             if any('_executeDrawCommand:' in f for f in s[4])),
        }
    total = sum(v['allSampleWeightSeconds'] for v in by_thread.values())
    draw = sum(v['drawInclusiveSeconds'] for v in by_thread.values())
    execute = sum(v['executeDrawInclusiveSeconds'] for v in by_thread.values())
    return {
        'pid': target_pid, 'traceDurationSeconds': duration,
        'sampleTimeBoundsSeconds': [min(s[0] for s in samples), max(s[0] for s in samples)],
        'recordedEncoderFrameCount': len(frame_ids),
        'frameOrdinalBounds': [min(frame_ids), max(frame_ids)],
        'drawingWorkerCount': len(drawing_threads), 'workers': by_thread,
        'aggregateWorkerSampleWeightSeconds': total,
        'aggregateDrawInclusiveSeconds': draw,
        'aggregateExecuteDrawInclusiveSeconds': execute,
        'aggregateWorkerWeightPerTraceSecond': total / duration,
        'aggregateDrawWeightPerTraceSecond': draw / duration,
        'aggregateWorkerSampleMSPerRecordedFrame': total * 1000 / len(frame_ids),
        'aggregateDrawInclusiveMSPerRecordedFrame': draw * 1000 / len(frame_ids),
        'inputs': [{'path': str(p), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
                   for p in inputs],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('raw', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = {name: analyze(args.raw, name) for name in ('early', 'loaded')}
    result['method'] = {
        'workerSelection': 'Every non-main thread with an observed drawRenderElement:withPass: stack; aggregate all samples on those tids. Draw-inclusive weights include only matching stacks.',
        'normalization': 'Use full finalized trace duration and unique target-process encoder frame ordinals. Boundary frames may be partial; values are statistical weights per recorded frame, not measured frame encoding latency.',
        'limitations': ['Locked-screen Phase 1 diagnostics; pose and device state were mismatched.',
                       'Nested draw and execute weights overlap and must not be added.',
                       'Thread sample weights can sum beyond trace duration due to parallel execution.',
                       'Does not measure GPU execution or exclusive hardware busy time.'],
    }
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    for name in ('early', 'loaded'):
        print(name, {k: v for k, v in result[name].items() if k not in ('workers', 'inputs')})


if __name__ == '__main__':
    main()
