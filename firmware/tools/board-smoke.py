#!/usr/bin/env python3
"""Run bounded local audio diagnostics; retain numeric levels only, never raw audio."""
import argparse,json,time
from pathlib import Path
from datetime import datetime,timezone
import serial
from esptool.reset import HardReset
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--port',default='/dev/cu.usbmodem1101')
p.add_argument('--cycles',type=int,default=10)
p.add_argument('--tone',action='store_true',help='Play one quiet half-second test tone')
a=p.parse_args()
if not 1<=a.cycles<=100:p.error('cycles must be between 1 and 100')
root=Path(__file__).resolve().parents[2]
folder=root/'work/board'/('audio-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
folder.mkdir(mode=0o700);events=[];pending=''
with serial.Serial(a.port,115200,timeout=.1) as port,(folder/'serial.txt').open('w') as log:
 def read_until(predicate,seconds):
  global pending
  end=time.monotonic()+seconds
  while time.monotonic()<end:
   text=port.read(4096).decode(errors='replace')
   if text:
    log.write(text);log.flush();pending+=text
    if any(x in text for x in ['stack overflow','Guru Meditation','Rebooting']):raise RuntimeError('Firmware crashed; inspect serial.txt')
    while '\n' in pending:
     line,pending=pending.split('\n',1)
     if line.startswith('{'):
      event=json.loads(line);events.append(event);print(json.dumps(event),flush=True)
   if predicate():return
  raise TimeoutError('Diagnostic response did not complete')
 port.dtr=False;HardReset(port,uses_usb=True)()
 read_until(lambda:any(e.get('test')=='ready' for e in events),8)
 if not any(e.get('psramBytes')==8388608 for e in events):raise RuntimeError('8 MB PSRAM missing')
 def command(c,test,count,seconds):
  before=len(events);port.write(c.encode());port.flush()
  read_until(lambda:sum(e.get('test')==test for e in events[before:])>=count,seconds)
  return [e for e in events[before:] if e.get('test')==test]
 if a.tone:
  tone=command('t','tone',1,3)[0]
  if not tone['ok']:raise RuntimeError('Speaker data transfer or mute failed')
 for cycle in range(a.cycles):
  levels=command('m','levels',4,5)
  if any(not e['ioOk'] or e['frames']!=32000 for e in levels):raise RuntimeError('Incomplete microphone transfer')
  resources=command('s','resources',1,2)[0]
  if resources['stackFreeMinBytes']<1024:raise RuntimeError('Insufficient measured stack margin')
 summary={'completedAt':datetime.now(timezone.utc).isoformat(),'cycles':a.cycles,'passed':True,'toneTransferTested':a.tone,'acousticOutputVerified':False,'minimumStackFreeBytes':min(e['stackFreeMinBytes'] for e in events if e.get('test')=='resources'),'events':events}
 (folder/'results.json').write_text(json.dumps(summary,indent=2)+'\n')
 print('Local diagnostic passed: '+str(folder),flush=True)
