#!/usr/bin/env python3
"""Local-only acoustic detector trial. Generated clips, no room-audio recording.
Requires bench wake observation mode; refuses to play until activation is suppressed.
"""
import argparse,json,os,time,subprocess
from pathlib import Path
import serial
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--manifest',default='work/board/hey-marvin-corpus/manifest.json');p.add_argument('--limit',type=int,default=40);p.add_argument('--computer-gain',type=float,default=1);a=p.parse_args()
if not 0<a.computer_gain<=1:p.error('computer gain must be in (0, 1]')
os.umask(0o077);root=Path(__file__).resolve().parents[2];manifest=root/a.manifest;items=json.loads(manifest.read_text())['items'][:a.limit]
port=serial.Serial(port=None,baudrate=115200,timeout=.1,write_timeout=1);port.dtr=False;port.rts=False;port.port='/dev/cu.usbmodem1101'
flash=json.loads((root/'work/board/afe-audible-flash.json').read_text())
application_sha=flash['images']['0x20000']['sha256']
pending=b'';events=[];candidates=[];stats={};suppressed=False;player=None;results=[];began=time.monotonic();last_status=0;case_peak=0;trial_started=False;last_afe=0

def poll(seconds):
 global pending,suppressed,last_status,case_peak,last_afe
 until=time.monotonic()+seconds
 while time.monotonic()<until:
  if time.monotonic()-last_status>1:port.write(b's' if suppressed else b'ws');last_status=time.monotonic()
  pending+=port.read(4096)
  while b'\n' in pending:
   line,pending=pending.split(b'\n',1)
   try:r=json.loads(line.decode())
   except (ValueError,UnicodeError):continue
   if 'afe' in r:
    last_afe=time.monotonic()
    if trial_started and not r['afe'].get('wakeActivationDisabled'):raise RuntimeError('Wake activation unexpectedly re-enabled')
   if 'wakeRuntime' in r:case_peak=max(case_peak,r['wakeRuntime'].get('intervalMaxProbability',0))
   if 'wakeActivationEnabled' in r:suppressed=r['wakeActivationEnabled'] is False
   if 'wakeIntervalPeak' in r:print(json.dumps({'wakeIntervalPeak':r['wakeIntervalPeak']}),flush=True)
   if 'wakeCandidate' in r:candidates.append({**r['wakeCandidate'],'hostTime':time.monotonic()});print(json.dumps(r),flush=True)
   if 'wakeDetectedUs' in r:events.append({'deviceUs':r['wakeDetectedUs'],'hostTime':time.monotonic()})
   for k in ('afe','audio','uplink','afeTiming','wakeRuntime','wakeInputPeak','wakeCandidates','wakeIntervalPeak'):
    if k in r:stats[k]=r[k]
   if 'audioFault' in r:raise RuntimeError('Firmware audio fault during detector trial')
   if 'linkFault' in r:stats['lastLinkFault']=r['linkFault']

try:
 port.open()
 while time.monotonic()-began<60:
  poll(1)
  if stats.get('audio',{}).get('available') and stats.get('afe',{}).get('processedSamples16k',0)>48000:
   port.write(b'w');poll(1)
   if suppressed and stats.get('afe',{}).get('wakeActivationDisabled'):break
 if not suppressed:raise RuntimeError('Wake-only observation mode not confirmed')
 baseline=json.loads(json.dumps(stats));baseline_at=time.monotonic();trial_started=True
 for i,item in enumerate(items):
  poll(3);case_peak=0;before=len(events);candidate_before=len(candidates);at=time.monotonic()
  player=subprocess.Popen(['/usr/bin/afplay','-v',str(a.computer_gain),str(manifest.parent/item['file'])],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  while player.poll() is None:poll(.1)
  if player.returncode:raise RuntimeError('Generated fixture playback failed')
  ended=time.monotonic();poll(2)
  found=events[before:];passed=len(found)==(1 if item['positive'] else 0)
  result={**item,'candidates':[{k:v for k,v in c.items() if k!='hostTime'} for c in candidates[candidate_before:]],'detections':len(found),'peakProbability':case_peak,'passed':passed,'clipSeconds':ended-at,'firstDetectionRelativeToClipEndMs':round((found[0]['hostTime']-ended)*1000) if found else None};results.append(result)
  print(json.dumps({'case':i,'positive':item['positive'],'detections':len(found),'peakProbability':case_peak,'passed':passed}),flush=True)
 poll(1)
 result={'scope':'Generated acoustic detector corpus, activation suppressed; not representative human or full wake acceptance','elapsedSeconds':time.monotonic()-began,'activationSuppressed':suppressed,'activationRemainsSuppressedAfterTest':True,'computerGain':a.computer_gain,'lastFlashedApplicationSha256':application_sha,'totalDetections':len(events),'baseline':baseline,'final':stats,'cases':results,'passed':all(x['passed'] for x in results) and len(results)==len(items) and stats.get('audio',{}).get('capturedSamples16k')==baseline.get('audio',{}).get('capturedSamples16k')}
 elapsed=time.monotonic()-baseline_at
 processed=stats.get('afe',{}).get('processedSamples16k',0)-baseline.get('afe',{}).get('processedSamples16k',0)
 unattributed=len(events)-sum(c['detections'] for c in results)
 result.update({'observationSeconds':elapsed,'processedSamplesDuringObservation':processed,'unattributedDetections':unattributed,'freshStatus':time.monotonic()-last_afe<3})
 result['passed']=result['passed'] and result['freshStatus'] and processed>=elapsed*16000*.9 and unattributed==0 and not stats.get('audio',{}).get('captureActive',True) and stats.get('uplink',{}).get('pcmSamples16k')==baseline.get('uplink',{}).get('pcmSamples16k')
except Exception as e:
 result={'scope':'Incomplete detector corpus','passed':False,'error':type(e).__name__,'message':str(e),'cases':results,'final':stats}
finally:
 if player and player.poll() is None:player.terminate()
 if port.is_open:
  try:port.write(b'xw')
  except serial.SerialException:pass
  port.close()
 dest=root/f'work/board/wake-corpus-{int(time.time()*1000)}.json';dest.write_text(json.dumps(result,indent=2)+'\n');print(str(dest),flush=True)
raise SystemExit(0 if result['passed'] else 1)
