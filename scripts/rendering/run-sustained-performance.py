#!/usr/bin/env python3
"""Run one controlled ten-minute native town route and its regression gate.

Build beforehand. Run clear and storm separately, without concurrent builds or
other simulator instances. The output directory must be new. GPU HUD logging is
a profiling fallback and adds overhead; callback cadence is not presentation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('output',type=Path);p.add_argument('--storm',action='store_true');p.add_argument('--daylight',type=float,default=.5)
p.add_argument('--gpu-hud',action='store_true',help='Separate instrumented profiling run; default tests normal production rendering')
a=p.parse_args()
root=Path(__file__).resolve().parents[2]
binary=root/'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator'
if not binary.is_file():p.error('Build the native app first')
if not 0<=a.daylight<=1:p.error('Daylight must be in [0,1]')
if a.output.exists():p.error('Use a new output directory to preserve earlier evidence')
# Never terminate another process: competing runs invalidate controlled timing.
existing=subprocess.run(['pgrep','-x','MarvinSimulator'],capture_output=True,text=True)
if existing.returncode==0:p.error('Another MarvinSimulator is running; finish it before this controlled run')
a.output=a.output.resolve();a.output.mkdir(parents=True)
metadata={'binarySHA256':hashlib.sha256(binary.read_bytes()).hexdigest(),'gitCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'gitStatus':subprocess.check_output(['git','status','--porcelain'],cwd=root,text=True),'storm':a.storm,'daylight':a.daylight,'durationSeconds':603,'gpuHUD':a.gpu_hud}
(a.output/'run-manifest.json').write_text(json.dumps(metadata,indent=2)+'\n')
env=dict(os.environ,MARVIN_BENCHMARK_SECONDS='603',MARVIN_DAYLIGHT_FRACTION=str(a.daylight),MARVIN_SANDSTORM='1' if a.storm else '0')
for key in ('MTL_HUD_ENABLED','MTL_HUD_LOG_ENABLED','MTL_HUD_ENCODER_TIMING_ENABLED'):
    env.pop(key,None)
if a.gpu_hud:env.update(MTL_HUD_ENABLED='1',MTL_HUD_LOG_ENABLED='1')
with (a.output/('native-metal.log' if a.gpu_hud else 'native.log')).open('w') as log:
    subprocess.run([str(binary),'--town-benchmark',str(a.output),'--city-roam'],env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
gate=[sys.executable,str(Path(__file__).with_name('check-sustained-performance.py')),str(a.output),'--scope','cadence']
if a.gpu_hud:
    subprocess.run([sys.executable,str(Path(__file__).with_name('profile-metal-timeline.py')),str(a.output)],check=True)
    gate += ['--gpu-report',str(a.output/'gpu-timeline.json')]
raise SystemExit(subprocess.run(gate).returncode)
