import subprocess,os,time,json,hashlib
from pathlib import Path
root=Path('/Users/thomi/Projects/marvee-perf-phase-1-baseline');raw=Path('/Users/thomi/Projects/marvin-town-planning/phase-1-baseline-2026-10-03');app=root/'apps/simulator-macos/.build/Marvin Simulator.app/Contents/MacOS/MarvinSimulator';out=raw/'metal-clear'
if out.exists():raise SystemExit('Refusing existing output')
if subprocess.run(['pgrep','-x','MarvinSimulator'],capture_output=True).returncode==0:raise SystemExit('Competing simulator')
env={k:os.environ[k] for k in ['HOME','USER','TMPDIR'] if k in os.environ};env.update(PATH='/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin',DEVELOPER_DIR='/Applications/Xcode.app/Contents/Developer',MARVIN_BENCHMARK_SECONDS='240',MARVIN_DAYLIGHT_FRACTION='0.5',MARVIN_SANDSTORM='0')
log=raw/'metal-clear-native.log';captures=[]
with log.open('w') as f:
 p=subprocess.Popen([str(app),'--town-benchmark',str(out),'--city-roam'],env=env,stdout=f,stderr=subprocess.STDOUT)
 identity=None
 until=time.monotonic()+90
 while time.monotonic()<until and p.poll() is None:
  lines=log.read_text().splitlines();v=[x for x in lines if x.startswith('MARVIN_BENCHMARK_ID ')]
  if v:identity=json.loads(v[0].split(' ',1)[1]);break
  time.sleep(.2)
 if identity is None:raise SystemExit('Missing native identity')
 for name,elapsed in [('early',60),('loaded',210)]:
  while p.poll() is None and time.monotonic()<identity['startUptime']+elapsed:time.sleep(.5)
  if p.poll() is not None:raise SystemExit('App ended before capture')
  cmd=['xcrun','xctrace','record','--template','Metal System Trace','--instrument','Time Profiler','--attach',str(p.pid),'--time-limit','10s','--output',str(raw/f'metal-{name}.trace')]
  item={'name':name,'pid':p.pid,'identity':identity,'command':cmd,'requestedElapsed':elapsed,'requestUptime':time.monotonic(),'requestWallTime':time.time()}
  with (raw/f'metal-{name}-record.log').open('w') as recordlog:item['exitCode']=subprocess.run(cmd,env=env,stdout=recordlog,stderr=subprocess.STDOUT).returncode
  item.update(finishedUptime=time.monotonic(),finishedWallTime=time.time());captures.append(item);(raw/'metal-capture-manifest.json').write_text(json.dumps({'binarySHA256':hashlib.sha256(app.read_bytes()).hexdigest(),'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'environment':env,'captureRuns':captures},indent=2)+'\n')
  print(json.dumps(item),flush=True)
 code=p.wait();print('Native diagnostic exit',code,flush=True)
