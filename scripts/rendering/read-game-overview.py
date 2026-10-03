#!/usr/bin/env python3
"""Inspect Apple Overview exports or import a strictly bound presentation ledger.

Export metal-perf-overview-layer-per-frame-interval-metric with xctrace first.
Keep process/layer identities separate. Cadence uses adjacent On Display starts;
Apple's exported interval ends have a calibrated 41–42 ns quantization gap.
Strict import: INTERVALS --benchmark BENCHMARK_JSON --manifest RUN_MANIFEST
  --toc TOC_XML --signposts SIGNPOST_XML --output PRESENTATION_JSON
Record Game Performance Overview with Points of Interest and a post-window tail.
MANIFEST must be finalized with benchmarkRunID, actualDurationSeconds, binary
SHA256, presentation=true, gpuHUD=false, weather/daylight and measured [3,603).
The gate reopens the manifest and raw exports; preserve them beside the report.
This verifies presentation evidence only, never GPU headroom or the weather pair.
"""
import argparse
import bisect
from collections import defaultdict
from decimal import Decimal, ROUND_HALF_EVEN
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

LAYER_NAME = 'SCNMetalBackingLayer: MarvinSimulator.SimulatorView (Per-Frame)'
PRESENTATION_KIND = 'appleGameOverviewOnDisplay'
NS = 1_000_000_000
CLOCK_TOLERANCE_NS = 100_000  # 0.1 ms; calibrated marker spread was 3.80 us.
END_QUANTIZATION_NS = 100  # Calibrated 41–42 ns; not a frame-loss allowance.
DISABLED_INSTRUMENTATION = {'MTL_CAPTURE_ENABLED', 'MTL_HUD_ENABLED',
                            'MTL_HUD_LOG_ENABLED', 'MTL_HUD_ENCODER_TIMING_ENABLED'}
CONTROLLED_MARVIN = {'MARVIN_BENCHMARK_SECONDS', 'MARVIN_DAYLIGHT_FRACTION', 'MARVIN_SANDSTORM'}


def seconds_ns(value):
    if isinstance(value, bool):
        raise ValueError('Boolean timestamp')
    number = Decimal(str(value))
    if not number.is_finite() or number < 0:
        raise ValueError('Invalid seconds value')
    return int((number * NS).to_integral_value(rounding=ROUND_HALF_EVEN))


def json_hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                     allow_nan=False).encode()).hexdigest()


def read_table(path, schema_name):
    """One export node only; refs are local to this XML, never other files."""
    root = ET.parse(path).getroot()
    nodes = root.findall('node')
    if len(nodes) != 1 or nodes[0].find('schema') is None:
        raise ValueError('Export exactly one run and one table')
    node = nodes[0]
    schema = node.find('schema')
    if schema.get('name') != schema_name:
        raise ValueError('Unexpected Apple export schema')
    definitions = [e for e in root.iter() if 'id' in e.attrib]
    ids = {e.get('id'): e for e in definitions}
    if len(ids) != len(definitions):
        raise ValueError('Duplicate XML definition ID')
    def resolve(e):
        if e is None:
            raise ValueError('Missing XML value')
        if 'ref' in e.attrib:
            if e.get('ref') not in ids:
                raise ValueError('Unresolved XML reference')
            return ids[e.get('ref')]
        return e
    columns = [c.findtext('mnemonic') for c in schema.findall('col')]
    if len(set(columns)) != len(columns):
        raise ValueError('Duplicate schema column')
    rows = []
    for row in node.findall('row'):
        if len(row) != len(columns):
            raise ValueError('Incomplete XML row')
        rows.append(dict(zip(columns, map(resolve, row))))
    match = re.search(r'/run\[(\d+)\]', node.get('xpath', ''))
    return rows, resolve, int(match[1]) if match else None


def read_presentation_rows(path):
    rows, resolve, run = read_table(path, 'metal-perf-overview-layer-per-frame-interval-metric')
    groups = defaultdict(lambda: defaultdict(list))
    for row in rows:
        pid = int(resolve(row['process'].find('pid')).text)
        name = row['name'].text or ''
        metric = name.split('.', 1)[-1]
        if metric not in ('On Display', 'GPU Begin to End', 'CPU Begin to Present'):
            continue
        label = re.search(r'\(Frame (\d+)\)', row['label'].text or '')
        if label is None:
            raise ValueError('Missing layer-local frame ID')
        key = (pid, row['process-name'].text, row['layer-id'].text, row['layer-name'].text)
        item = {'frame': int(label[1])}
        for field in ('start', 'end', 'duration'):
            value = row[field]
            item[field] = None if value.tag == 'sentinel' else int(value.text)
        groups[key][metric].append(item)
    return groups, run


def read_benchmark_markers(path, run_id, pid):
    rows, resolve, run = read_table(path, 'os-signpost')
    markers = {}
    def raw_text(e):
        e = resolve(e)
        return (e.text or '') + ''.join(raw_text(child) for child in e)
    for row in rows:
        if row['subsystem'].text != 'io.entire.marvin.performance':
            continue
        name = row['name'].text
        if name not in ('TownBenchmarkStart', 'TownBenchmarkEnd'):
            continue
        text = raw_text(row['message'])  # Never localized/truncated fmt attributes.
        match = re.fullmatch(r'run=(\S+) markerUptime=([\d.]+) benchmarkBoundaryUptime=([\d.]+)', text)
        if match is None:
            raise ValueError('Malformed benchmark marker')
        if match[1] != run_id or int(resolve(row['process'].find('pid')).text) != pid:
            raise ValueError('Marker run/PID mismatch')
        if name in markers:
            raise ValueError('Duplicate benchmark marker')
        markers[name] = {'traceNS': int(row['time'].text),
                         'markerUptimeNS': seconds_ns(match[2]),
                         'boundaryUptimeNS': seconds_ns(match[3])}
    if set(markers) != {'TownBenchmarkStart', 'TownBenchmarkEnd'}:
        raise ValueError('Both benchmark markers are required')
    return markers, run


def presentation_clock(markers, benchmark):
    start, end = (markers[k] for k in ('TownBenchmarkStart', 'TownBenchmarkEnd'))
    for marker in (start, end):
        if any(type(marker[k]) is not int or marker[k] < 0 for k in
               ('traceNS', 'markerUptimeNS', 'boundaryUptimeNS')):
            raise ValueError('Invalid marker timestamp')
        if marker['markerUptimeNS'] < marker['boundaryUptimeNS']:
            raise ValueError('Marker precedes its benchmark boundary')
    if end['traceNS'] <= start['traceNS'] or end['markerUptimeNS'] <= start['markerUptimeNS']:
        raise ValueError('Unordered benchmark markers')
    offsets = [m['markerUptimeNS'] - m['traceNS'] for m in (start, end)]
    if abs(offsets[1] - offsets[0]) > CLOCK_TOLERANCE_NS:
        raise ValueError('Benchmark marker clock offsets disagree')
    if abs(start['boundaryUptimeNS'] - seconds_ns(benchmark['startUptime'])) > 1_000:
        raise ValueError('Benchmark start identity mismatch')
    if abs(end['boundaryUptimeNS'] - start['boundaryUptimeNS'] -
           seconds_ns(benchmark['durationSeconds'])) > 1_000:
        raise ValueError('Benchmark end/duration identity mismatch')
    return offsets[0], abs(offsets[1] - offsets[0])


def verify_environment(environment, benchmark):
    """Allow only the runner's production controls, never unexplained overrides."""
    for key, value in environment.items():
        if not isinstance(key, str) or not isinstance(value, str):
            raise ValueError('Invalid trace environment')
        if key.startswith(('__XPC_MARVIN_', '__XPC_MTL_HUD_', '__XPC_MTL_CAPTURE_')):
            raise ValueError('Unexplained inherited diagnostic environment')
        if key.startswith('MARVIN_') and key not in CONTROLLED_MARVIN:
            raise ValueError(f'Unapproved simulator environment override: {key}')
        if key.startswith(('MTL_HUD_', 'MTL_CAPTURE_')) and (key not in DISABLED_INSTRUMENTATION or value != '0'):
            raise ValueError(f'Capture/HUD instrumentation is not production evidence: {key}')
    if 'MARVIN_SANDSTORM' in environment and environment['MARVIN_SANDSTORM'] != ('1' if benchmark['sandstorm'] else '0'):
        raise ValueError('Trace weather disagrees with benchmark')
    if 'MARVIN_DAYLIGHT_FRACTION' in environment and Decimal(environment['MARVIN_DAYLIGHT_FRACTION']) != Decimal(str(benchmark['daylightFraction'])):
        raise ValueError('Trace daylight disagrees with benchmark')
    if 'MARVIN_BENCHMARK_SECONDS' in environment and not 603*NS <= seconds_ns(environment['MARVIN_BENCHMARK_SECONDS']) <= seconds_ns(benchmark['durationSeconds']):
        raise ValueError('Trace requested duration disagrees with completed benchmark')


def verify_presentation_ledger(data, benchmark):
    """Low-level numerical checks only; not a saved-report provenance verifier."""
    result = {'presentationVerified': False, 'presentationGatePassed': False,
              'failures': [], 'overallComplete': False}
    try:
        if type(data.get('schemaVersion')) is not int or data['schemaVersion'] != 1 or data.get('metricKind') != PRESENTATION_KIND:
            raise ValueError('Unsupported presentation report')
        if data.get('benchmarkSHA256') != json_hash(benchmark) or not benchmark.get('benchmarkRunID') or data.get('runID') != benchmark['benchmarkRunID']:
            raise ValueError('Presentation evidence belongs to a different benchmark')
        if not re.fullmatch(r'[0-9a-f]{64}', data.get('binarySHA256', '')):
            raise ValueError('Missing binary identity')
        if data.get('layerName') != LAYER_NAME or data.get('processName') != 'MarvinSimulator' or type(data.get('pid')) is not int or data['pid'] <= 0 or not str(data.get('layerID', '')).isdigit():
            raise ValueError('Unverified simulator process/layer identity')
        offset, spread = presentation_clock(data['markers'], benchmark)
        trace = data['trace']
        if trace.get('pid') != data['pid'] or trace.get('template') != 'Game Performance Overview' or trace.get('runNumber') != 1 or trace.get('endReason') != 'Target app exited':
            raise ValueError('Incomplete or mismatched trace identity')
        if trace.get('returnExitStatus') != '0':
            raise ValueError('Trace target did not complete normally')
        if type(trace.get('durationNS')) is not int or trace['durationNS'] < data['markers']['TownBenchmarkEnd']['traceNS']:
            raise ValueError('Trace ends before benchmark completion')
        if benchmark.get('durationSeconds', 0) < 603:
            raise ValueError('Less than 600 measured seconds after warmup')
        start = seconds_ns(benchmark['startUptime']) + 3 * NS - offset
        end = start + 600 * NS
        if start < 0 or end > trace['durationNS']:
            raise ValueError('Measured window lies outside trace')
        verify_environment(trace.get('environment', {}), benchmark)
        metrics = data['intervals']
        if set(metrics) != {'On Display', 'CPU Begin to Present', 'GPU Begin to End'}:
            raise ValueError('Missing frame lifecycle metrics')
        complete, pending = {}, []
        for metric, rows in metrics.items():
            mapped = {}
            for row in rows:
                frame = row['frame']
                if type(frame) is not int or frame < 0 or frame in mapped:
                    raise ValueError('Duplicate/invalid layer-local frame record')
                mapped[frame] = row
                values = [row[k] for k in ('start', 'end', 'duration')]
                if any(value is None for value in values):
                    # Only known post-window work may remain pending. Never infer
                    # an actual presentation from CPU/GPU work or a sentinel.
                    if type(row['start']) is not int or not end <= row['start'] <= trace['durationNS']:
                        raise ValueError('Unresolved interior/boundary frame event')
                    pending.append({'metric': metric, 'frame': frame})
                elif any(type(value) is not int or value < 0 for value in values) or row['duration'] <= 0 or row['duration'] != row['end'] - row['start']:
                    raise ValueError('Invalid frame interval')
                elif row['end'] > trace['durationNS'] + END_QUANTIZATION_NS:
                    raise ValueError('Frame interval exceeds trace coverage')
            complete[metric] = {f: r for f, r in mapped.items() if all(r[k] is not None for k in ('start', 'end', 'duration'))}
        display = sorted(complete['On Display'].values(), key=lambda r: r['start'])
        times = [r['start'] for r in display]
        if any(b <= a for a, b in zip(times, times[1:])):
            raise ValueError('Duplicate/unordered display starts')
        left, right = bisect.bisect_right(times, start) - 1, bisect.bisect_left(times, end)
        if left < 0 or right >= len(display):
            raise ValueError('Actual display starts must bracket the full measured window')
        bracket = display[left:right+1]
        for a, b in zip(bracket, bracket[1:]):
            if b['frame'] != a['frame'] + 1:
                raise ValueError('Dropped or missing interior presentation event; cause unclassified')
            if abs(b['start'] - a['end']) > END_QUANTIZATION_NS:
                raise ValueError('Display intervals do not cover adjacent presentation starts')
        for row in bracket:
            for metric in ('CPU Begin to Present', 'GPU Begin to End'):
                upstream = complete[metric].get(row['frame'])
                if upstream is None or upstream['end'] > row['start'] + END_QUANTIZATION_NS:
                    raise ValueError('Missing or inconsistent presented-frame lifecycle')
            # These are the same layer-local frame, not adjacent-frame timing.
            # GPU work may overlap CPU encoding/presentation; it cannot precede
            # that frame's CPU beginning. Do not assume CPU-end <= GPU-start.
            cpu = complete['CPU Begin to Present'][row['frame']]
            gpu = complete['GPU Begin to End'][row['frame']]
            if gpu['start'] < cpu['start'] - END_QUANTIZATION_NS:
                raise ValueError('GPU interval precedes its frame CPU beginning')
        # CPU/GPU frames without On Display are never promoted to presentations.
        for metric in ('CPU Begin to Present', 'GPU Begin to End'):
            for frame, row in complete[metric].items():
                if frame not in complete['On Display']:
                    if row['start'] < end and row['end'] >= start or bracket[0]['frame'] <= frame <= bracket[-1]['frame']:
                        raise ValueError('Unpresented or lost interior frame; cause unclassified')
                    pending.append({'metric': metric, 'frame': frame})
        intervals = [min(end, b['start']) - max(start, a['start']) for a, b in zip(bracket, bracket[1:])]
        selected = [r for r in display if start <= r['start'] < end]
        windows = [(bisect.bisect_left(times, start+(s+10)*NS) - bisect.bisect_left(times, start+s*NS))/10 for s in range(591)]
        gaps = sum(dt > 25_000_000 for dt in intervals)
        result.update(presentationVerified=True, measuredFrames=len(selected), gapsOver25MS=gaps,
                      minimumRolling10SecondFPS=min(windows), clockOffsetSpreadNS=spread,
                      measuredTraceWindowNS=[start, end], boundaryOrPendingEvents=pending,
                      displayIntervalDistribution=distribution(intervals))
        if gaps:
            result['failures'].append(f'{gaps} measured display holds exceed 25 ms')
        if len(selected) < 35998:
            result['failures'].append('Displayed-frame coverage below 35998/36000')
        if min(windows) < 59.8:
            result['failures'].append('Rolling displayed-frame coverage below 598/600')
        result['presentationGatePassed'] = not result['failures']
    except (ValueError, TypeError, KeyError, IndexError, AttributeError, ArithmeticError) as error:
        result['failures'].append(f'Invalid presentation evidence: {error}')
    return result


def verify_manifest(data, benchmark, manifest):
    if manifest.get('benchmarkRunID') != benchmark['benchmarkRunID']:
        raise ValueError('Finalized manifest benchmark identity mismatch')
    if data.get('manifestSHA256') != json_hash(manifest):
        raise ValueError('Finalized manifest hash mismatch')
    if data.get('binarySHA256') != manifest.get('binarySHA256') or not re.fullmatch(r'[0-9a-f]{64}', manifest.get('binarySHA256', '')):
        raise ValueError('Manifest binary identity mismatch')
    if manifest.get('actualDurationSeconds') != benchmark['durationSeconds']:
        raise ValueError('Manifest actual duration differs from benchmark')
    if manifest.get('measuredWindowSeconds') != [3,603] or manifest.get('measurementWindowEndExclusive') is not True or manifest.get('presentation') is not True:
        raise ValueError('Manifest does not describe the presentation acceptance window')
    if type(manifest.get('storm')) is not bool or type(benchmark.get('sandstorm')) is not bool or manifest['storm'] != benchmark['sandstorm'] or type(manifest.get('daylight')) not in (int,float) or manifest['daylight'] != benchmark['daylightFraction']:
        raise ValueError('Manifest weather/daylight differs from benchmark')
    if manifest.get('gpuHUD') is not False:
        raise ValueError('Manifest does not disable HUD instrumentation')


def verify_presentation(data, benchmark, manifest=None):
    """Acceptance path: bind manifest and reimport hashed raw evidence.

    A self-consistent saved ledger is insufficient. Reimporting also proves its
    values and identities actually came from the declared raw exports.
    """
    try:
        verify_manifest(data, benchmark, manifest)
        sources = data['sourcePaths']
        if set(sources) != {'intervals','signposts','toc'}:
            raise ValueError('Missing raw export source identities')
        expected = {str(Path(path).resolve()): hashlib.sha256(Path(path).read_bytes()).hexdigest() for path in sources.values()}
        if len(expected) != 3 or data.get('sourceSHA256') != expected:
            raise ValueError('Raw export hashes differ or are missing')
        imported = import_presentation(*(Path(sources[k]) for k in ('intervals','signposts','toc')), benchmark, manifest)
        without_validation = lambda report: {k:v for k,v in report.items() if k != 'validation'}
        if json_hash(without_validation(data)) != json_hash(without_validation(imported)):
            raise ValueError('Saved presentation report differs from its raw exports')
        result = imported['validation']
        result['provenanceVerified'] = True
        return result
    except (OSError, ValueError, TypeError, KeyError, IndexError, AttributeError, ArithmeticError, ET.ParseError) as error:
        return {'presentationVerified':False, 'presentationGatePassed':False, 'provenanceVerified':False,
                'overallComplete':False, 'failures':[f'Invalid presentation provenance: {error}']}


def import_presentation(intervals, signposts, toc, benchmark, manifest):
    if manifest.get('benchmarkRunID') != benchmark['benchmarkRunID']:
        raise ValueError('Manifest benchmark identity mismatch')
    tree = ET.parse(toc).getroot()
    runs = tree.findall('run')
    if len(runs) != 1 or runs[0].get('number') != '1':
        raise ValueError('Exactly one trace run is required')
    run = runs[0]
    process = run.find('info/target/process')
    pid = int(process.get('pid'))
    if process.get('name') != 'MarvinSimulator':
        raise ValueError('Wrong trace target')
    groups, interval_run = read_presentation_rows(intervals)
    matches = [(key, rows) for key, rows in groups.items() if key[0] == pid and key[1] == 'MarvinSimulator' and key[3] == LAYER_NAME]
    if len(matches) != 1:
        raise ValueError('SimulatorView layer is missing or ambiguous')
    markers, marker_run = read_benchmark_markers(signposts, benchmark['benchmarkRunID'], pid)
    if interval_run != 1 or marker_run != 1:
        raise ValueError('Exports are not bound to trace run 1')
    key, rows = matches[0]
    environment_items = run.findall('info/target/environment/item')
    environment = {e.get('key'): e.get('value') for e in environment_items}
    if len(environment) != len(environment_items):
        raise ValueError('Duplicate trace environment key')
    report = {'schemaVersion': 1, 'metricKind': PRESENTATION_KIND,
              'benchmarkSHA256': json_hash(benchmark), 'runID': benchmark['benchmarkRunID'],
              'binarySHA256': manifest['binarySHA256'], 'manifestSHA256':json_hash(manifest), 'pid': pid, 'processName': key[1],
              'layerID': key[2], 'layerName': key[3], 'markers': markers, 'intervals': dict(rows),
              'trace': {'runNumber': 1, 'pid': pid,
                        'template': run.findtext('info/summary/template-name'),
                        'durationNS': seconds_ns(run.findtext('info/summary/duration')),
                        'endReason': run.findtext('info/summary/end-reason'),
                        'returnExitStatus': process.get('return-exit-status'),
                        'environment': environment},
              'sourcePaths': {key:str(Path(path).resolve()) for key,path in zip(('intervals','signposts','toc'),(intervals,signposts,toc))},
              'sourceSHA256': {str(Path(path).resolve()): hashlib.sha256(Path(path).read_bytes()).hexdigest() for path in (intervals, signposts, toc)},
              'limitations': 'Recorded Apple presentation events only. Unclassified interior omissions fail closed. No instrumentation-overhead correction, GPU headroom proof, or paired clear/storm acceptance.'}
    verify_manifest(report, benchmark, manifest)
    report['validation'] = verify_presentation_ledger(report, benchmark)
    report['validation']['provenanceVerified'] = True
    return report


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('intervals',type=Path)
    parser.add_argument('--output',type=Path)
    for flag in ('benchmark', 'manifest', 'toc', 'signposts'):
        parser.add_argument('--'+flag, type=Path)
    args = parser.parse_args()
    binding = [args.benchmark, args.manifest, args.toc, args.signposts]
    if any(binding) and not all(binding):
        parser.error('--benchmark, --manifest, --toc and --signposts are required together')
    strict = all(binding)
    try:
        result = (import_presentation(args.intervals, args.signposts, args.toc,
                  json.loads(args.benchmark.read_text()), json.loads(args.manifest.read_text()))
                  if strict else summarize(read_intervals(args.intervals)))
    except (OSError, ValueError, TypeError, KeyError, AttributeError, ArithmeticError, ET.ParseError) as error:
        result = {'validation': {'presentationVerified': False, 'presentationGatePassed': False,
                  'overallComplete': False, 'failures': [f'Invalid presentation import: {error}']}}
        strict = True
    text = json.dumps(result,indent=2)+'\n'
    if args.output:
        args.output.write_text(text)
    print(json.dumps(result['validation'], indent=2) if strict else text, end='\n' if strict else '')
    # A slow but fully accounted recording is valid evidence, not a cadence pass.
    return 0 if not strict or result['validation']['presentationVerified'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
