#!/usr/bin/env python3
"""Like-for-like --town-benchmark run of the macOS game or the Godot port at an exact drawable size.

    run-benchmark.py OUT --app macos|godot|export [--size 1920x1080] [--mode city-roam|race|postrace-roam|dune-roam]
                     [--seconds 45] [--godot-binary PATH] [--export-binary PATH] [--trace AT:SECONDS [--labels]]
                     [--flag GAME-FLAG] [-- game flags]

--flag=GAME-FLAG runs another game mode in the same window and with the same recording (`GAME-FLAG OUT`, e.g.
--flag=--loading-smoke-test; write it with "=" so the flag is not read as an option)
instead of --town-benchmark.

Window: both games get their window from window-inject.m (DYLD_INSERT_LIBRARIES; the macOS game is not modified). The
benchmark's 960 x 540-point window request (TownSmoke.swift; the Godot facade's setContentSize) becomes a window of
exactly SIZE points at the bottom left of the screen, at the floating level (above other apps, as Godot's
MARVIN_BENCHMARK_ON_TOP), so on this 1x display the drawable is SIZE pixels in both games and their HUDs have the same
point size. Godot runs at backing scale 1 (no MARVIN_BACKING_SCALE). A black backdrop window covers the rest of the
screen (one level below), so other apps' windows are occluded in every run: a MarvinSimulator left running on this Mac
used 400-500 GPU-ms per second while visible and stopped drawing when covered, which the macOS game's opaque window
did at 1080p and Godot's window did not.

GPU: `metalperftrace listen` (Apple's always-on Metal performance statistics, no HUD overhead) records every Metal
process that presents frames, once per second: the game's FPS on glass, on-GPU walltime per frame (min/mean/max) and
frame-on-glass intervals, and the GPU time of the other processes (contention, e.g. a MarvinSimulator left running).
--trace AT:SECONDS also records a Metal System Trace (xctrace) AT seconds after launch, or, with --trace-from-start, AT
seconds after the benchmark started (its MARVIN_BENCHMARK_ID line), which does not depend on how long loading took.
An exported release build does not flush its stdout per line, so the line arrives only when the game exits: trace an
export from launch instead (the benchmark starts 11-12 s after launch on an M2). xctrace leaves its raw kernel trace
(about 1 GB per recording) in the temporary directory; the runner deletes the ones its recording created.
--env MARVIN_BENCHMARK_FREEZE=S stops the benchmark's drive and camera at benchmark time S and keeps drawing that view
(Godot only), so a trace after S measures one fixed frame and can be compared across builds.

Shared machine: --wait-idle waits until no other Godot or Marvin Simulator process runs (MARVIN_PERF_IGNORE_PIDS: a
comma-separated list of PIDs to ignore, e.g. a game left running), and every run records the other game processes that
were alive during it in run.json "contention" (rerun when it is not empty: parallel GPU work skews every number).

Main thread: main-stalls.m logs every gap of 50 ms or more between turns of the main run loop (OUT/main-stalls.txt), in
both games, which shows loading freezes and long frames whether or not a Metal layer is presenting.
--labels also loads metal-labels.m, which names every Metal encoder by its attachments and pipelines for the trace.

Writes OUT/run.json (summary), OUT/metal-listen.json, OUT/process.json (RSS and CPU per second), OUT/game.log and the
benchmark's own files (benchmark.json, timeline.json, resources.json, displayed-fps.json, final-fps.png; Godot also
godot-render.json). Run one GPU-heavy process at a time.
"""
import argparse
import datetime
import json
import os
import signal
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
GODOT_ROOT = HERE.parents[1]
REPO = HERE.parents[3]
MAC_BINARY = REPO / 'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
GODOT_BINARY = Path(os.environ.get('GODOT', Path.home() / 'Applications/Godot_mono.app/Contents/MacOS/Godot'))
MODES = {'city-roam': ['--city-roam'], 'race': [], 'postrace-roam': ['--postrace-roam'], 'dune-roam': ['--dune-roam'],
         'outer-town-survey': ['--outer-town-survey']}


def parse_objects(text):
    """metalperftrace listen --json prints pretty-printed JSON objects back to back."""
    decoder, i, out = json.JSONDecoder(), 0, []
    while i < len(text):
        while i < len(text) and text[i].isspace():
            i += 1
        if i >= len(text):
            break
        try:
            obj, i = decoder.raw_decode(text, i)
        except json.JSONDecodeError:
            break
        out.append(obj)
    return out


def percentile(values, p):
    if not values:
        return None
    values = sorted(values)
    return values[min(len(values) - 1, int((len(values) - 1) * p))]


def dist(values):
    if not values:
        return None
    return {'mean': statistics.mean(values), 'p50': percentile(values, .5), 'p95': percentile(values, .95),
            'p99': percentile(values, .99), 'max': max(values), 'count': len(values)}


def summarize_metal(records, pid, start, end):
    """Per-second Metal statistics of `pid` inside [start, end] (Unix seconds) and the GPU time of other processes."""
    rows, others = [], {}
    for r in records:
        stamp = datetime.datetime.fromisoformat(r['Date'].replace('Z', '+00:00')).timestamp()
        if not (start <= stamp <= end):
            continue
        for layer in r.get('Layers', []):
            stats = layer.get('Performance Stats', {})
            presented = stats.get('Presented Frame Stats', {})
            gpu = presented.get('On-GPU Walltime Stats', {})
            if r['PID'] != pid:
                key = f"{r['Process']} ({r['PID']})"
                others.setdefault(key, []).append(gpu.get('Total (ms)', 0) / max(stats.get('Total Duration (sec)', 1), 1e-6))
                continue
            glass = stats.get('Frame-On-Glass Interval Stats', {})
            skipped = stats.get('Skipped Frame Stats', {})
            rows.append({'t': stamp, 'duration': stats.get('Total Duration (sec)', 0), 'frames': presented.get('Frame Count', 0),
                         'gpuTotal': gpu.get('Total (ms)', 0), 'gpuCount': gpu.get('Count', 0), 'gpuMean': gpu.get('Average (ms)', 0),
                         'gpuMax': gpu.get('Max (ms)', 0), 'glassMax': glass.get('Max (ms)', 0), 'glassMean': glass.get('Average (ms)', 0),
                         'skipped': skipped.get('Frame Count', 0),
                         'cpuMean': presented.get('End-to-end Walltime Stats (CPU)', {}).get('Average (ms)', 0),
                         'e2eMean': presented.get('End-to-end Walltime Stats (Total)', {}).get('Average (ms)', 0)})
    if not rows:
        return {'samples': 0}
    duration = sum(r['duration'] for r in rows)
    gpu_count = sum(r['gpuCount'] for r in rows)
    return {
        'method': 'metalperftrace listen: per-second Presented Frame Stats of the game layer (On-GPU Walltime per frame, frame-on-glass intervals)',
        'seconds': len(rows), 'onGlassFPS': sum(r['frames'] for r in rows) / duration if duration else None,
        'gpuMSPerFrame': sum(r['gpuTotal'] for r in rows) / gpu_count if gpu_count else None,
        'gpuBusyMSPerSecond': sum(r['gpuTotal'] for r in rows) / duration if duration else None,
        'gpuPerSecondMean': dist([r['gpuMean'] for r in rows if r['gpuCount']]),
        'gpuFrameMaxMS': max(r['gpuMax'] for r in rows),
        'glassIntervalPerSecondMax': dist([r['glassMax'] for r in rows]),
        'skippedFrames': sum(r['skipped'] for r in rows),
        'cpuEndToEndMeanMS': statistics.mean(r['cpuMean'] for r in rows),
        'otherProcessesGPUMSPerSecond': {k: statistics.mean(v) for k, v in others.items()},
    }


def summarize_timeline(path):
    if not path.exists():
        return {}
    t = json.loads(path.read_text())
    frames, updates = t.get('renderFrames', []), t.get('updates', [])
    out = {'renderIntervalMS': dist([f[1] for f in frames]), 'renderCallbackSpanMS': dist([f[2] for f in frames])}
    if updates:
        cols = t['updateColumns']
        for name in ('physicsMS', 'modelsMS', 'effectsMS', 'cameraMS', 'townMS', 'totalMS'):
            out['update.' + name] = dist([u[cols.index(name)] for u in updates])
    return out


def summarize_stalls(path, launch, start=None, end=None):
    """main-stalls.txt: '<seconds since the probe started> <gap ms>' per gap of 50 ms or more (the probe starts at launch)."""
    if not path.exists():
        return {}
    gaps = []
    for line in path.read_text().splitlines():
        if line.startswith('#') or not line.strip():
            continue
        t, ms = (float(v) for v in line.split())
        wall = launch + t
        if start is not None and not (start <= wall <= end):
            continue
        gaps.append((t, ms))
    if not gaps:
        return {'over50': 0, 'over100': 0, 'over250': 0}
    t, ms = max(gaps, key=lambda g: g[1])
    return {'over50': len(gaps), 'over100': sum(g > 100 for _, g in gaps), 'over250': sum(g > 250 for _, g in gaps),
            'longestMS': ms, 'longestAt': round(t, 2), 'totalStallSeconds': sum(g for _, g in gaps) / 1000,
            'worst': sorted(([round(t, 2), g] for t, g in gaps), key=lambda g: -g[1])[:10]}


# The Godot editor, exported builds of the port and the macOS game (any build of either).
GAME_PATTERN = r'Godot_mono\.app/Contents/MacOS/Godot|Marvin Simulator\.app/Contents/MacOS/|MarvinSimulator'


def leftover_ktraces():
    """Instruments' raw kernel traces in the temporary directories (xctrace does not delete them after a recording)."""
    dirs = {Path(tempfile.gettempdir()), Path(os.environ.get('TMPDIR', tempfile.gettempdir()))}
    return {f for d in dirs if d.is_dir() for f in d.glob('instruments*.ktrace')}


def other_games(own_pids):
    """PIDs of other running Godot / Marvin Simulator processes (not ours, not MARVIN_PERF_IGNORE_PIDS)."""
    ignore = {int(p) for p in os.environ.get('MARVIN_PERF_IGNORE_PIDS', '').split(',') if p.strip()}
    out = subprocess.run(['pgrep', '-f', GAME_PATTERN], capture_output=True, text=True).stdout.split()
    return sorted(int(p) for p in out if int(p) not in own_pids and int(p) not in ignore and int(p) != os.getpid())


def wait_idle(quiet_seconds=1.0, poll=0.25, limit=7200):
    """Wait until no other game process has run for quiet_seconds (at most limit seconds); returns the time waited."""
    t0, quiet_since = time.time(), None
    while time.time() - t0 < limit:
        if other_games(set()):
            quiet_since = None
        elif quiet_since is None:
            quiet_since = time.time()
        elif time.time() - quiet_since >= quiet_seconds:
            return time.time() - t0
        time.sleep(poll)
    return time.time() - t0


def watch_contention(own_pid, seen, stop):
    while not stop.is_set():
        for pid in other_games({own_pid}):
            seen.add(pid)
        stop.wait(1.0)


def sample_process(pid, out, stop):
    while not stop.is_set():
        try:
            line = subprocess.run(['ps', '-o', 'rss=,%cpu=', '-p', str(pid)], capture_output=True, text=True).stdout.split()
            if len(line) == 2:
                out.append({'t': time.time(), 'rssMB': int(line[0]) / 1024, 'cpuPercent': float(line[1])})
        except Exception:
            pass
        stop.wait(1.0)


def main():
    argv = sys.argv[1:]
    extra = []
    if '--' in argv:
        i = argv.index('--')
        argv, extra = argv[:i], argv[i + 1:]
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('out', type=Path)
    p.add_argument('--app', choices=['macos', 'godot', 'export'], required=True)
    p.add_argument('--size', default='960x540')
    p.add_argument('--mode', choices=sorted(MODES), default='city-roam')
    p.add_argument('--seconds', type=float, default=45)
    p.add_argument('--daylight', default='0.5')
    p.add_argument('--godot-binary', type=Path, default=GODOT_BINARY)
    p.add_argument('--export-binary', type=Path)
    p.add_argument('--probes', type=Path, default=Path(os.environ.get('MARVIN_PERF_PROBES', Path(tempfile.gettempdir()) / 'marvin-perf')),
                   help='directory of the built probes (tools/perf/build-probes.sh; built there when missing)')
    p.add_argument('--inject', type=Path, help='(old) path of window-inject.dylib; its directory is used as --probes')
    p.add_argument('--labels', action='store_true', help='load metal-labels.dylib (encoder names for --trace)')
    p.add_argument('--flag', help='game mode to run instead of --town-benchmark (it gets OUT as its directory)')
    p.add_argument('--level', default='3')
    p.add_argument('--no-backdrop', action='store_true', help='do not cover the rest of the screen with a black window')
    p.add_argument('--trace', help='AT:SECONDS: record a Metal System Trace AT seconds after launch (or after the benchmark start)')
    p.add_argument('--trace-from-start', action='store_true', help='--trace AT counts from the benchmark start, not from launch')
    p.add_argument('--wait-idle', action='store_true', help='wait until no other Godot / Marvin Simulator process runs')
    p.add_argument('--godot-args', default='', help='extra Godot engine arguments (space separated), e.g. --audio-driver Dummy')
    p.add_argument('--env', action='append', default=[], help='KEY=VALUE for the game')
    a = p.parse_args(argv)
    w, h = (int(v) for v in a.size.split('x'))
    if a.inject:
        a.probes = a.inject.parent
    if not all((a.probes / f).exists() for f in ('window-inject.dylib', 'metal-labels.dylib', 'main-stalls.dylib')):
        subprocess.run([str(HERE / 'build-probes.sh'), str(a.probes)], check=True)
    libraries = [a.probes / 'window-inject.dylib', a.probes / 'main-stalls.dylib'] + ([a.probes / 'metal-labels.dylib'] if a.labels else [])
    a.out = a.out.resolve()
    a.out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    for key in ('MTL_HUD_ENABLED', 'MTL_HUD_LOG_ENABLED', 'MARVIN_BACKING_SCALE', 'MARVIN_BENCHMARK_ON_TOP'):
        env.pop(key, None)
    env.update(MARVIN_BENCHMARK_SECONDS=str(a.seconds), MARVIN_DAYLIGHT_FRACTION=a.daylight,
               DYLD_INSERT_LIBRARIES=':'.join(str(l) for l in libraries), MARVIN_INJECT_FRAME=f'0,0,{w},{h}', MARVIN_INJECT_LEVEL=a.level,
               MARVIN_INJECT_BACKDROP='0' if a.no_backdrop else '1', MARVIN_STALL_LOG=str(a.out / 'main-stalls.txt'))
    if a.labels:
        env['MARVIN_METAL_LABELS'] = '1'
    for kv in a.env:
        k, v = kv.split('=', 1)
        env[k] = v
    game = ([a.flag, str(a.out)] if a.flag else ['--town-benchmark', str(a.out)] + MODES[a.mode]) + extra
    if a.app == 'macos':
        cmd = [str(MAC_BINARY)] + game
    else:
        env['DOTNET_ROOT'] = env.get('DOTNET_ROOT', str(Path.home() / '.dotnet'))
        env['PATH'] = env['DOTNET_ROOT'] + ':/opt/homebrew/bin:' + env.get('PATH', '')
        engine = a.godot_args.split() if a.godot_args else []
        if a.app == 'godot':
            cmd = [str(a.godot_binary), '--path', str(GODOT_ROOT)] + engine + ['--'] + game
        else:
            if not a.export_binary:
                p.error('--export-binary is required for --app export')
            cmd = [str(a.export_binary)] + engine + ['--'] + game
    meta = {'app': a.app, 'size': [w, h], 'mode': a.flag or a.mode, 'seconds': a.seconds, 'command': cmd, 'launchWallTime': time.time(),
            'env': {k: env[k] for k in sorted(env) if k.startswith(('MARVIN_', 'DYLD_', 'MTL_'))}}
    if a.wait_idle:
        meta['waitedForIdleSeconds'] = wait_idle()
        meta['launchWallTime'] = time.time()
    listen_path = a.out / 'metal-listen.json'
    with open(a.out / 'game.log', 'w') as log, open(listen_path.with_suffix('.raw'), 'w') as listen_out:
        listener = subprocess.Popen(['metalperftrace', 'listen', '--json'], stdout=listen_out, stderr=subprocess.DEVNULL)
        game_proc = subprocess.Popen(cmd, env=env, stdout=log, stderr=subprocess.STDOUT, cwd=str(GODOT_ROOT))
        samples, stop = [], threading.Event()
        sampler = threading.Thread(target=sample_process, args=(game_proc.pid, samples, stop), daemon=True)
        sampler.start()
        contention = set()
        watcher = threading.Thread(target=watch_contention, args=(game_proc.pid, contention, stop), daemon=True)
        watcher.start()
        tracer = None
        if a.trace:
            at, secs = (float(v) for v in a.trace.split(':'))
            def record():
                if a.trace_from_start:
                    log_path = a.out / 'game.log'
                    while game_proc.poll() is None and 'MARVIN_BENCHMARK_ID' not in log_path.read_text(errors='replace'):
                        time.sleep(0.2)
                time.sleep(at)
                if game_proc.poll() is None:
                    before = leftover_ktraces()
                    subprocess.run(['xctrace', 'record', '--template', 'Metal System Trace', '--attach', str(game_proc.pid),
                                    '--time-limit', f'{secs:g}s', '--output', str(a.out / 'metal-system.trace')],
                                   stdout=open(a.out / 'xctrace.log', 'w'), stderr=subprocess.STDOUT)
                    for path in leftover_ktraces() - before:
                        path.unlink(missing_ok=True)
            tracer = threading.Thread(target=record, daemon=True)
            tracer.start()
        try:
            code = game_proc.wait(timeout=a.seconds + 600)
        except subprocess.TimeoutExpired:
            game_proc.kill()
            code = 'timeout'
        stop.set()
        if tracer:
            tracer.join(timeout=120)
        time.sleep(1.5)
        listener.send_signal(signal.SIGINT)
        try:
            listener.wait(timeout=10)
        except subprocess.TimeoutExpired:
            listener.kill()
    records = parse_objects(listen_path.with_suffix('.raw').read_text())
    listen_path.write_text(json.dumps(records))
    listen_path.with_suffix('.raw').unlink()
    (a.out / 'process.json').write_text(json.dumps(samples))
    meta['exitWallTime'] = time.time()
    summary = {'run': meta, 'exitCode': code, 'contention': sorted(contention)}
    summary['mainStalls'] = summarize_stalls(a.out / 'main-stalls.txt', meta['launchWallTime'])
    bench = a.out / 'benchmark.json'
    if bench.exists():
        b = json.loads(bench.read_text())
        end = b['endWallTime']
        start = end - b['durationSeconds'] + 3
        summary['benchmark'] = {k: b.get(k) for k in ('meanFPS', 'p50MS', 'p95MS', 'p99MS', 'over25MS', 'over50MS', 'samples', 'drawableWidth',
                                                     'drawableHeight', 'cpuUpdateP95MS', 'durationSeconds', 'daylightFraction', 'visiblePeople', 'godot')}
        summary['timeline'] = summarize_timeline(a.out / 'timeline.json')
        summary['metal'] = summarize_metal(records, game_proc.pid, start, end)
        inside = [s for s in samples if start <= s['t'] <= end]
        if inside:
            summary['process'] = {'rssMB': dist([s['rssMB'] for s in inside]), 'cpuPercent': dist([s['cpuPercent'] for s in inside])}
        gr = a.out / 'godot-render.json'
        if gr.exists():
            g = json.loads(gr.read_text())
            summary['godotRender'] = {k: g.get(k) for k in ('processMS', 'flushMS', 'dispatchMS', 'meshesBuiltPerFrame', 'flushStagesMS', 'frameSetupCpuMS')}
        summary['benchmarkWindow'] = {'startAfterLaunchSeconds': start - meta['launchWallTime'], 'endAfterLaunchSeconds': end - meta['launchWallTime']}
        summary['mainStallsInBenchmark'] = summarize_stalls(a.out / 'main-stalls.txt', meta['launchWallTime'], start, end)
    else:
        summary['metal'] = summarize_metal(records, game_proc.pid, meta['launchWallTime'], meta['exitWallTime'])
    trace = a.out / 'metal-system.trace'
    if a.trace and trace.exists():
        # GPU per pass of this run's process (tools/perf/mst-gpu.py), for the frames presented during the trace.
        r = subprocess.run([sys.executable, str(HERE / 'mst-gpu.py'), str(trace), '--process', f'({game_proc.pid})',
                            '--json', str(a.out / 'mst-gpu.json')], capture_output=True, text=True)
        (a.out / 'mst-gpu.txt').write_text(r.stdout + r.stderr)
        if (a.out / 'mst-gpu.json').exists():
            mst = json.loads((a.out / 'mst-gpu.json').read_text())
            ignore = [p for p in os.environ.get('MARVIN_PERF_IGNORE_PIDS', '').split(',') if p.strip()]
            busy = {k: v['busyMSPerSecond'] for k, v in mst['processes'].items()}
            # Other processes doing GPU work during the trace (not this game, WindowServer or ignored PIDs; more than 100 ms
            # of GPU time in all, so a few stray intervals do not count): the per-pass numbers of a run with any are skewed.
            others = {k: v['busyMSPerSecond'] for k, v in mst['processes'].items()
                      if v['busyMSPerSecond'] * v['spanSeconds'] > 100 and f'({game_proc.pid})' not in k
                      and not k.startswith('WindowServer') and not any(f'({p})' in k for p in ignore)}
            summary['traceGPU'] = {'busyMSPerFrame': mst.get('busyMSPerFrame'), 'frames': mst.get('frames'),
                                   'processesBusyMSPerSecond': busy, 'contention': others}
    (a.out / 'run.json').write_text(json.dumps(summary, indent=2) + '\n')
    b, m = summary.get('benchmark', {}), summary.get('metal', {})
    if b:
        print(f"{a.app:6s} {a.mode:10s} {b['drawableWidth']:.0f}x{b['drawableHeight']:.0f}  fps {b['meanFPS']:6.2f}  p50 {b['p50MS']:5.2f}  "
              f"p95 {b['p95MS']:5.2f}  p99 {b['p99MS']:5.2f}  >25ms {b['over25MS']:4d}  glass {m.get('onGlassFPS') or 0:6.2f} fps  "
              f"GPU {m.get('gpuMSPerFrame') or 0:5.2f} ms/frame ({m.get('gpuBusyMSPerSecond') or 0:4.0f} ms/s)  "
              f"others {', '.join(f'{k} {v:.0f}' for k, v in (m.get('otherProcessesGPUMSPerSecond') or {}).items())}"
              + (f"  CONTENTION {sorted(contention)}" if contention else '')
              + (f"  trace GPU busy {summary['traceGPU']['busyMSPerFrame']:.2f} ms/frame" if 'traceGPU' in summary else '')
              + (f"  TRACE CONTENTION {summary['traceGPU']['contention']}" if summary.get('traceGPU', {}).get('contention') else ''))
    else:
        st = summary['mainStalls']
        print(f"{a.app:6s} {a.flag or a.mode} {a.size}: exit {code}, {meta['exitWallTime'] - meta['launchWallTime']:.1f} s; main-thread stalls "
              f">=100 ms {st.get('over100')}, longest {st.get('longestMS')} ms at {st.get('longestAt')} s; glass {m.get('onGlassFPS') or 0:.1f} fps")
    return 0 if code == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
