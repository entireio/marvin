#!/usr/bin/env python3
"""Fail closed on sustained town regressions. Callback cadence is NOT presentation.

Usage: check-sustained-performance.py RUN_DIRECTORY [--gpu-report GPU_JSON]
       [--presentation-report PRESENTATION_JSON]
GPU_JSON must contain per-minute p99MS/maxMS plus a measured duration. The
presentation scope requires both callback and actual display cadence. Complete
acceptance still needs full GPU coverage and a paired clear/storm result.
"""
import argparse
import bisect
import importlib.util
import json
import math
from pathlib import Path

_spec = importlib.util.spec_from_file_location('presentation_overview', Path(__file__).with_name('read-game-overview.py'))
presentation_overview = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(presentation_overview)


def evaluate(report, timeline, hud, gpu=None, presentation=None, manifest=None):
    failures = []
    def require(ok, reason):
        if not ok:
            failures.append(reason)
    frames = timeline.get('renderFrames', [])
    updates = timeline.get('updates', [])
    start = report.get('startUptime', 0) + 3
    end = start + 600
    require(report.get('durationSeconds', 0) >= 603, 'Less than 600 measured seconds after 3-second warmup')
    require(report.get('drawableWidth') == 1920 and report.get('drawableHeight') == 1080, 'Not native 1920x1080')
    require(report.get('metalRenderer') and report.get('townEnabled') and report.get('audioActive'), 'Production Metal/town/audio missing')
    flags = report.get('benchmarkArguments', [])
    require(isinstance(flags,list), 'Invalid benchmark arguments')
    if not isinstance(flags,list): flags=[]
    require('--city-roam' in flags, 'Continuous city route missing')
    require(all(not f.startswith('--') or f in {'--town-benchmark','--city-roam','--benchmark-timer','--benchmark-display-link'} for f in flags), 'Unapproved benchmark mode or diagnostic flag')
    forbidden = {'--benchmark-no-shadows', '--without-town', '--benchmark-simple-town', '--benchmark-no-deformation', '--benchmark-isolate-trails', '--benchmark-msaa2', '--benchmark-transparent-ground', '--dune-roam', '--outer-town-survey', '--postrace-roam', '--benchmark-no-sand-shadows', '--benchmark-no-contact-coating'}
    require(not forbidden.intersection(flags), 'Diagnostic quality/isolation flags present')
    quality=report.get('quality',{})
    require(quality.get('msaaSamples')==2 and quality.get('shadowMapWidths')==[2048,4096] and quality.get('explorationDetail') is True, 'Missing or reduced runtime quality manifest')
    require(bool(frames) and all(len(f) >= 2 and all(math.isfinite(v) for v in f) for f in frames), 'Missing or nonfinite frame ledger')
    times = [f[0] for f in frames]
    require(all(b > a for a, b in zip(times, times[1:])), 'Unordered or duplicate frame timestamps')
    # Validate the complete ledger so deleting a long frame cannot hide a gap.
    require(all(abs((b[0]-a[0])*1000-b[1]) < .01 for a,b in zip(frames,frames[1:])), 'Frame intervals disagree with timestamps')
    selected = [f for f in frames if start <= f[0] <= end]
    require(bool(selected) and selected[0][0] <= start+.04 and selected[-1][0] >= end-.04, 'Incomplete measured frame coverage')
    gaps = sum(f[1] > 25 for f in selected)
    require(gaps == 0, f'{gaps} render callback gaps exceed 25 ms')
    windows=[]
    # Ten-second windows every second. Two boundary frames of uncertainty only.
    for second in range(591):
        a=start+second;b=a+10
        count=bisect.bisect_left(times,b)-bisect.bisect_left(times,a)
        windows.append(count/10)
    require(min(windows,default=0) >= 59.8, 'Rolling 10-second coverage below 598/600 frames')
    require(len(selected) >= 35998, 'Full-run coverage below 35998/36000 frames')
    require(bool(updates) and updates[0][0] <= start+.04 and updates[-1][0] >= end-.04, 'Incomplete driving update ledger')
    require(all(len(u)>=12 and all(math.isfinite(v) for v in u) for u in updates), 'Invalid driving values')
    require(all(0 < b[0]-a[0] <= .1 and math.hypot(b[9]-a[9],b[10]-a[10]) <= (b[0]-a[0])*10+.01 for a,b in zip(updates,updates[1:])), 'Driving ledger gap, teleport or implausible speed')
    measured_updates=[u for u in updates if start <= u[0] <= end]
    distance=sum(math.hypot(b[9]-a[9],b[10]-a[10]) for a,b in zip(measured_updates,measured_updates[1:]) if len(a)>10 and len(b)>10)
    update_times=[u[0] for u in measured_updates]
    require(len(measured_updates)>=35998, 'Insufficient simulation-update coverage for 60 Hz motion')
    update_windows=[(bisect.bisect_left(update_times,start+second+10)-bisect.bisect_left(update_times,start+second))/10 for second in range(591)]
    require(min(update_windows,default=0)>=59.8, 'Rolling simulation-update rate below 59.8 Hz')
    for second in range(0,600,30):
        portion=[u for u in measured_updates if start+second<=u[0]<start+second+30]
        travel=sum(math.hypot(b[9]-a[9],b[10]-a[10]) for a,b in zip(portion,portion[1:]))
        require(travel>=10, f'Insufficient driving during seconds {second}–{second+30}')
    require(all(b-a<=.025 for a,b in zip(update_times,update_times[1:])), 'Simulation-update gap exceeds 25 ms')
    require(distance >= 1000, 'Less than 1000 m of real driving')
    finite_hud=[h for h in hud if isinstance(h.get('fps'),(int,float)) and math.isfinite(h['fps']) and isinstance(h.get('elapsedSeconds'),(int,float)) and math.isfinite(h['elapsedSeconds'])]
    require(len(finite_hud)==len(hud), 'Invalid live counter value')
    # The post-window tail exists to close actual presentation intervals. It is
    # not part of the measured drive, nor is startup before the warmup boundary.
    valid_hud=[h for h in finite_hud if 3 <= h['elapsedSeconds'] <= 603]
    require(len(valid_hud) >= 1100, 'Missing live FPS samples')
    require(bool(valid_hud) and valid_hud[0]['elapsedSeconds'] <= 4.1 and valid_hud[-1]['elapsedSeconds'] >= 602, 'Incomplete live counter coverage')
    require(all(b['elapsedSeconds']-a['elapsedSeconds'] <= 1 for a,b in zip(valid_hud,valid_hud[1:])), 'Live counter sample gap')
    require(all(h['fps']>=59 for h in valid_hud), 'Live counter falls below 59 FPS')
    for a,b in zip(valid_hud,valid_hud[1:]):
        dt=b['elapsedSeconds']-a['elapsedSeconds']
        if dt<=0:
            require(False,'Unordered live counter timestamps');break
        count=bisect.bisect_right(times,report.get('startUptime',0)+b['elapsedSeconds'])-bisect.bisect_right(times,report.get('startUptime',0)+a['elapsedSeconds'])
        if abs(count/dt-b['fps'])>2.1:
            require(False,'Live counter disagrees with rendered-frame ledger');break
    callback_passed=not failures
    require(gpu is not None, 'GPU headroom evidence missing')
    if gpu is not None:
        require(gpu.get('metricKind')=='encoderBusyUnion', 'GPU headroom unverified: validated encoder busy-time evidence required')
        minutes=gpu.get('minutes',[])
        require(bool(report.get('benchmarkRunID')) and gpu.get('runID')==report.get('benchmarkRunID') and gpu.get('runStartUptime')==report.get('startUptime') and gpu.get('gpuDevice')==report.get('gpuDevice') and gpu.get('resolution')==[1920,1080], 'GPU evidence belongs to a different or unidentified run')
        require(gpu.get('measuredSeconds',0)>=599 and len(minutes)==10, 'Incomplete GPU headroom coverage (1 second batch-boundary tolerance)')
        require(all(all(isinstance(m.get(k),(int,float)) and math.isfinite(m[k]) and m[k]>=0 for k in ['samples','firstElapsed','lastElapsed','maximumBatchGapSeconds']) and m.get('start',math.inf)<=m['firstElapsed']<=m['lastElapsed']<=m.get('end',-math.inf) for m in minutes), 'Invalid GPU coverage fields')
        require(all(m.get('samples',0)>=1000 and m.get('lastElapsed',0)-m.get('firstElapsed',0)>=57 and m.get('maximumBatchGapSeconds',math.inf)<=1.1 for m in minutes), 'Sparse or missing GPU timeline')
        require(all(m.get('start')==3+i*60 and m.get('end')==3+(i+1)*60 for i,m in enumerate(minutes)), 'GPU minute windows do not match benchmark')
        require(all(all(isinstance(m.get(k),(int,float)) and math.isfinite(m[k]) and m[k]>=0 for k in ['p99MS','maxMS']) and m['p99MS']<=12.5 and m['maxMS']<1000/60 for m in minutes), 'GPU budget exceeded or invalid (p99 12.5 ms, max 16.667 ms)')
    presentation_result = (presentation_overview.verify_presentation(presentation, report, manifest) if presentation is not None
                           else {'presentationVerified': False, 'presentationGatePassed': False,
                                 'failures': ['Presentation ledger missing']})
    failures.extend(presentation_result['failures'])
    return {'passed':not failures,'callbackGatePassed':callback_passed,
            'presentationVerified':presentation_result['presentationVerified'],
            'presentationGatePassed':presentation_result['presentationGatePassed'],
            'presentation':presentation_result,'overallComplete':False,
            'completionBlockers':['GPU aggregate samples do not establish complete per-frame busy-time coverage.',
                                  'A paired clear/storm acceptance result is not implemented.'],
            'failures':failures,'measuredFrames':len(selected),'gapsOver25MS':gaps,
            'minimumRolling10SecondFPS':min(windows,default=0),'distanceMeters':distance,
            'method':'Callback and driving ledgers plus independently revalidated Apple On Display presentation ledger. GPU headroom and paired-weather completion remain separately required.'}


def scope_passed(result, scope):
    if scope == 'presentation':
        return all(result.get(key) is True for key in ('callbackGatePassed', 'presentationVerified', 'presentationGatePassed'))
    return result.get('callbackGatePassed' if scope == 'cadence' else 'overallComplete') is True


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path);p.add_argument('--gpu-report',type=Path);p.add_argument('--presentation-report',type=Path);p.add_argument('--scope',choices=['complete','cadence','presentation'],default='complete');a=p.parse_args()
    try:
        result=evaluate(*(json.loads((a.directory/n).read_text()) for n in ['benchmark.json','timeline.json','displayed-fps.json']),json.loads(a.gpu_report.read_text()) if a.gpu_report else None,json.loads(a.presentation_report.read_text()) if a.presentation_report else None,json.loads((a.directory/'run-manifest.json').read_text()) if a.presentation_report else None)
    except (OSError,ValueError,TypeError,KeyError,IndexError,AttributeError,OverflowError,ZeroDivisionError) as e:
        result={'passed':False,'failures':[f'Invalid or missing evidence: {e}']}
    result['requestedScope']=a.scope
    result['scopePassed']=scope_passed(result,a.scope)
    (a.directory/'sustained-gate.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2));return 0 if result['scopePassed'] else 1

if __name__=='__main__':
    raise SystemExit(main())
