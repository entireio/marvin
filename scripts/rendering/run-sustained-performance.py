#!/usr/bin/env python3
"""Run a controlled ten-minute native town route and its regression gate.

Build beforehand. Run clear and storm separately without concurrent builds or
simulators. Use a new output directory. Default: production callback cadence.
--presentation records Apple's Game Performance Overview and Points of Interest,
including three seconds of real driving after the measured [3,603) window.
Neither a cadence nor a presentation pass proves GPU headroom or both weathers.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

INTERVAL_SCHEMA = 'metal-perf-overview-layer-per-frame-interval-metric'
SIGNPOST_SCHEMA = 'os-signpost'
SIGNPOST_XPATH = ('/trace-toc/run[1]/data/table[@schema="os-signpost" '
                  'and @category="PointsOfInterest" and @target-pid="SINGLE" '
                  'and not(@subsystem) and not(@dynamic-tracing-enabled-subsystems)]')
DISABLED_INSTRUMENTATION = {
    'MTL_CAPTURE_ENABLED': '0',
    'MTL_HUD_ENABLED': '0',
    'MTL_HUD_LOG_ENABLED': '0',
    'MTL_HUD_ENCODER_TIMING_ENABLED': '0',
}


class RunFailure(Exception):
    def __init__(self, stage, message, returncode=1):
        super().__init__(message)
        self.stage = stage
        self.returncode = returncode or 1


def make_plan(root, output, *, storm=False, daylight=.5, gpu_hud=False,
              presentation=False, inherited_environment=None):
    """Pure command/environment planning; never launches a process."""
    if gpu_hud and presentation:
        raise ValueError('--gpu-hud and --presentation are mutually exclusive')
    if not 0 <= daylight <= 1:
        raise ValueError('Daylight must be in [0,1]')
    # Match BinaryDaylight's supported sunrise/sunset bounds. Keep the original
    # request separately rather than labelling a clamped scene with that value.
    applied_daylight = max(.015, min(.985, daylight))
    root, output = Path(root), Path(output)
    binary = root/'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
    duration = 606 if presentation else 603
    inherited = os.environ if inherited_environment is None else inherited_environment
    # A stale diagnostic from an earlier investigation must not enter acceptance.
    prefixes = ('MARVIN_', '__XPC_MARVIN_', 'MTL_HUD_', 'MTL_CAPTURE_',
                '__XPC_MTL_HUD_', '__XPC_MTL_CAPTURE_')
    environment = {k: v for k, v in inherited.items() if not k.startswith(prefixes)}
    controlled = dict(DISABLED_INSTRUMENTATION,
                      MARVIN_BENCHMARK_SECONDS=str(duration),
                      MARVIN_DAYLIGHT_FRACTION=str(applied_daylight),
                      MARVIN_SANDSTORM='1' if storm else '0')
    if gpu_hud:
        controlled.update(MTL_HUD_ENABLED='1', MTL_HUD_LOG_ENABLED='1')
    environment.update(controlled)
    scripts = root/'scripts/rendering'
    app = [str(binary), '--town-benchmark', str(output), '--city-roam']
    gate = [sys.executable, str(scripts/'check-sustained-performance.py'), str(output),
            '--scope', 'presentation' if presentation else 'cadence']
    plan = {'binary': binary, 'output': output, 'environment': environment,
            'requestedDaylight': daylight, 'daylight': applied_daylight,
            'controlledEnvironment': controlled, 'requestedDurationSeconds': duration,
            'storm': storm,
            'mode': 'presentation' if presentation else 'gpu-hud' if gpu_hud else 'cadence',
            'launch': app, 'exports': [], 'import': None, 'profile': None, 'gate': gate}
    if presentation:
        # xcode-select may still point to CommandLineTools, which lacks xctrace.
        environment.setdefault('DEVELOPER_DIR', '/Applications/Xcode.app/Contents/Developer')
        trace = output/'presentation.trace'
        record = ['xcrun', 'xctrace', 'record', '--template', 'Game Performance Overview',
                  '--instrument', 'Points of Interest', '--time-limit', '660s',
                  '--output', str(trace), '--target-stdout', str(output/'native.log')]
        # Explicit target env avoids depending on Instruments launch inheritance.
        for key, value in sorted(controlled.items()):
            record += ['--env', f'{key}={value}']
        plan['launch'] = record + ['--launch', '--'] + app
        plan['trace'] = trace
        plan['exports'] = [
            ('toc', ['xcrun', 'xctrace', 'export', '--input', str(trace), '--toc',
                     '--output', str(output/'toc.xml')], output/'toc.xml'),
            ('intervals', ['xcrun', 'xctrace', 'export', '--input', str(trace), '--xpath',
                           f'/trace-toc/run[1]/data/table[@schema="{INTERVAL_SCHEMA}"]',
                           '--output', str(output/'intervals.xml')], output/'intervals.xml'),
            ('signposts', ['xcrun', 'xctrace', 'export', '--input', str(trace), '--xpath',
                           SIGNPOST_XPATH,
                           '--output', str(output/'signposts.xml')], output/'signposts.xml'),
        ]
        plan['import'] = [sys.executable, str(scripts/'read-game-overview.py'),
                          str(output/'intervals.xml'), '--benchmark', str(output/'benchmark.json'),
                          '--manifest', str(output/'run-manifest.json'), '--toc', str(output/'toc.xml'),
                          '--signposts', str(output/'signposts.xml'), '--output', str(output/'presentation.json')]
        gate += ['--presentation-report', str(output/'presentation.json')]
    elif gpu_hud:
        plan['profile'] = [sys.executable, str(scripts/'profile-metal-timeline.py'), str(output)]
        gate += ['--gpu-report', str(output/'gpu-timeline.json')]
    return plan


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False)+'\n')


def require_file(path, stage):
    if not path.is_file() or path.stat().st_size == 0:
        raise RunFailure(stage, f'Missing or empty output: {path.name}')



def verify_export_tables(toc):
    """Reject ambiguous/missing tables instead of selecting an arbitrary first."""
    root = ET.parse(toc).getroot()
    runs = root.findall('run')
    if root.tag != 'trace-toc' or len(runs) != 1 or runs[0].get('number') != '1':
        raise RunFailure('export-toc', 'Expected exactly trace run 1')
    tables = runs[0].findall('data/table')
    intervals = [t for t in tables if t.get('schema') == INTERVAL_SCHEMA]
    markers = [t for t in tables if t.get('schema') == SIGNPOST_SCHEMA and
               t.get('category') == 'PointsOfInterest' and t.get('target-pid') == 'SINGLE' and
               'subsystem' not in t.attrib and 'dynamic-tracing-enabled-subsystems' not in t.attrib]
    if len(intervals) != 1 or len(markers) != 1:
        raise RunFailure('export-toc', 'Missing or ambiguous per-frame interval / unrestricted Points of Interest table')


def execute_plan(plan, manifest, *, run=None):
    """Execute sequentially. A recording/export/import failure never reaches gate."""
    run = subprocess.run if run is None else run
    output, environment = plan['output'], plan['environment']
    stage = 'record' if plan['mode'] == 'presentation' else 'native'

    def step(command, name, log_name):
        with (output/log_name).open('w') as log:
            result = run(command, env=environment, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RunFailure(name, f'{name} failed; see {log_name}', result.returncode)

    try:
        log = 'recording.log' if plan['mode'] == 'presentation' else 'native-metal.log' if plan['mode'] == 'gpu-hud' else 'native.log'
        step(plan['launch'], stage, log)
        if plan['mode'] == 'presentation':
            trace = plan['trace']
            if not trace.is_dir() or not any(trace.iterdir()):
                raise RunFailure('record', 'Recording returned without a nonempty trace bundle')
        stage = 'benchmark'
        require_file(output/'benchmark.json', stage)
        benchmark = json.loads((output/'benchmark.json').read_text())
        if not isinstance(benchmark, dict):
            raise RunFailure(stage, 'Benchmark report is not an object')
        duration = benchmark.get('durationSeconds')
        if (isinstance(duration, bool) or not isinstance(duration, (int, float)) or
                not math.isfinite(duration) or duration < plan['requestedDurationSeconds']):
            raise RunFailure(stage, 'Benchmark did not finish the requested drive, including its real tail')
        if benchmark.get('sandstorm') is not plan['storm']:
            raise RunFailure(stage, 'Recorded storm state differs from the requested weather')
        daylight = benchmark.get('daylightFraction')
        if (isinstance(daylight, bool) or not isinstance(daylight, (int, float)) or
                not math.isfinite(daylight) or daylight != plan['daylight']):
            raise RunFailure(stage, 'Recorded daylight differs from the requested daylight')
        run_id = benchmark.get('benchmarkRunID')
        if not isinstance(run_id, str) or not run_id:
            raise RunFailure(stage, 'Completed benchmark has no run identity')
        if hashlib.sha256(plan['binary'].read_bytes()).hexdigest() != manifest['binarySHA256']:
            raise RunFailure(stage, 'Built binary changed during the run')
        manifest.update(benchmarkRunID=run_id, actualDurationSeconds=duration)
        write_json(output/'run-manifest.json', manifest)
        for name, command, path in plan['exports']:
            stage = 'export-'+name
            step(command, stage, stage+'.log')
            require_file(path, stage)
            if name == 'toc':
                verify_export_tables(path)
        if plan['import'] is not None:
            stage = 'presentation-import'
            # Importer returns 1 for structurally invalid evidence. A valid but
            # slow ledger returns 0 and must reach the presentation cadence gate.
            step(plan['import'], stage, stage+'.log')
            require_file(output/'presentation.json', stage)
        if plan['profile'] is not None:
            stage = 'gpu-profile'
            step(plan['profile'], stage, stage+'.log')
            require_file(output/'gpu-timeline.json', stage)
        stage = 'gate'
        result = run(plan['gate'], env=environment)
        write_json(output/'runner-status.json',
                   {'stage': stage, 'returnCode': result.returncode, 'mode': plan['mode'],
                    'evidencePipelineCompleted': True, 'overallComplete': False})
        return result.returncode
    except (RunFailure, OSError, ValueError, TypeError, ET.ParseError) as error:
        code = error.returncode if isinstance(error, RunFailure) else 1
        write_json(output/'runner-status.json',
                   {'stage': error.stage if isinstance(error, RunFailure) else stage,
                    'returnCode': code, 'mode': plan['mode'], 'error': str(error),
                    'evidencePipelineCompleted': False, 'overallComplete': False})
        print(str(error), file=sys.stderr)
        return code


def argument_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--storm', action='store_true')
    parser.add_argument('--daylight', type=float, default=.5)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--gpu-hud', action='store_true', help='Separate HUD profiling run; adds overhead')
    modes.add_argument('--presentation', action='store_true', help='Record and gate Apple On Display presentation events')
    return parser


def main(argv=None):
    parser = argument_parser()
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[2]
    if not 0 <= args.daylight <= 1:
        parser.error('Daylight must be in [0,1]')
    if args.output.exists():
        parser.error('Use a new output directory to preserve earlier evidence')
    plan = make_plan(root, args.output.resolve(), storm=args.storm, daylight=args.daylight,
                     gpu_hud=args.gpu_hud, presentation=args.presentation)
    if not plan['binary'].is_file():
        parser.error('Build the native app first')
    # Never terminate another process: competing runs invalidate controlled timing.
    existing = subprocess.run(['pgrep', '-x', 'MarvinSimulator'], capture_output=True, text=True)
    if existing.returncode == 0:
        parser.error('Another MarvinSimulator is running; finish it before this controlled run')
    if existing.returncode != 1:
        parser.error('Could not check for another MarvinSimulator process')
    plan['output'].mkdir(parents=True)
    metadata = {
        'binarySHA256': hashlib.sha256(plan['binary'].read_bytes()).hexdigest(),
        'gitCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
        'gitStatus': subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True),
        'storm': args.storm, 'daylight': plan['daylight'],
        'requestedDaylight': plan['requestedDaylight'],
        'requestedDurationSeconds': plan['requestedDurationSeconds'],
        'measuredWindowSeconds': [3, 603], 'measurementWindowEndExclusive': True,
        'gpuHUD': args.gpu_hud, 'presentation': args.presentation,
        'controlledEnvironment': plan['controlledEnvironment'], 'launchCommand': plan['launch'],
        'overallComplete': False,
    }
    write_json(plan['output']/'run-manifest.json', metadata)
    return execute_plan(plan, metadata)


if __name__ == '__main__':
    raise SystemExit(main())
