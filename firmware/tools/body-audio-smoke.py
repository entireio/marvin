"""Bounded local acoustic smoke: synthesized computer speech, real board mic and speaker.
No microphone samples are saved. Stop is sent even if the test fails.
"""
import time,json,subprocess,os,argparse
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--fixture',type=str,default='work/board/body-question.aiff')
p.add_argument('--computer-gain',type=float,default=.5)
p.add_argument('--manual',action='store_true',help='Wait for a human wake phrase; no computer audio or USB voice start')
p.add_argument('--speaker-volume',type=int,choices=range(0,101,5),help='Set bench speaker volume before stimulus; steps of five')
p.add_argument('--barge-fixture',type=str,help='Play a second acoustic request after one second of board playback')
p.add_argument('--wake',action='store_true',help='Play the fixture before listening; never activate voice through USB')
p.add_argument('--interrupt',action='store_true',help='Interrupt after playback starts, then verify output stays stopped')
a=p.parse_args()
if a.manual:a.wake=True
if not 0<a.computer_gain<=1:p.error('Use gain greater than zero and at most 1')
from pathlib import Path
import serial
os.umask(0o077)
root=Path(__file__).resolve().parents[2]
port=serial.Serial(port=None,baudrate=115200,timeout=.1,write_timeout=1);port.dtr=False;port.rts=False;port.port='/dev/cu.usbmodem1101'
records=[];log=bytearray();pending=b'';listening=None;play=None;started=time.monotonic();last_request=0;last_status=0;result={};audible=False;failed=False;interrupted=None;played_at_interrupt=None;afe_ready=False;online=False;wake_armed=False;wake_confirmed=False;wake_requested=False;barge=None;speaker_volume=None;volume_requested=False;announced=False
try:
 port.open()
 port.write(b'xws')
 while (time.monotonic()-listening<25 if a.manual and listening else time.monotonic()-started<(120 if a.manual else 75)):
  now=time.monotonic()
  if now-last_status>(.25 if a.interrupt and listening else 2):port.write(b's');last_status=now
  if a.speaker_volume is not None and speaker_volume is not None and not volume_requested:
   delta=a.speaker_volume-speaker_volume;port.write((b'+' if delta>0 else b'-')*(abs(delta)//5));volume_requested=True
  if a.wake and afe_ready and online and not wake_requested:
   port.write(b'Ws');wake_requested=True
  if (a.speaker_volume is None or speaker_volume==a.speaker_volume) and audible and (not a.wake or (afe_ready and online and wake_armed)) and not listening and now-started>15 and now-last_request>5:
   if a.manual:
    if not announced:print('Board ready for human wake and interruption test.',flush=True);announced=True
   elif a.wake:
    if play is None:play=subprocess.Popen(['/usr/bin/afplay','-v',str(a.computer_gain),str(root/a.fixture)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
   else:port.write(b'v')
   last_request=now
  data=port.read(4096);log.extend(data);pending+=data
  while b'\n' in pending:
   line,pending=pending.split(b'\n',1)
   try:
    record=json.loads(line.decode());records.append(record)
    if record.get('audio',{}).get('silent') is False:audible=True
    if record.get('afe',{}).get('processedSamples16k',0)>=48000:afe_ready=True
    if 'uplink' in record:online=record['uplink'].get('online',False)
    if 'wakeActivationEnabled' in record:
     wake_armed=record['wakeActivationEnabled'] is True
     wake_confirmed=wake_confirmed or (wake_requested and wake_armed)
    if 'speakerVolume' in record:speaker_volume=record['speakerVolume']
    if listening and ('linkFault' in record or 'audioFault' in record):failed=True
    if a.barge_fixture and listening and barge is None and record.get('audio',{}).get('playedSamples16k',0)>=16000:
     barge=subprocess.Popen(['/usr/bin/afplay','-v',str(a.computer_gain),str(root/a.barge_fixture)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    if a.interrupt and listening and interrupted is None and record.get('audio',{}).get('playedSamples16k',0)>=1600:
     played_at_interrupt=record['audio']['playedSamples16k'];port.write(b'is');interrupted=now
    if record.get('voice')=='listening' and not listening:
     listening=now
     if not a.wake:play=subprocess.Popen(['/usr/bin/afplay','-v',str(a.computer_gain),str(root/a.fixture)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
   except (ValueError,UnicodeError):pass
  if failed or (interrupted and now-interrupted>3) or (listening and now-listening>25):break
 port.write(b'xws');end=time.monotonic()+4
 while time.monotonic()<end:
  data=port.read(4096);log.extend(data);pending+=data
  while b'\n' in pending:
   line,pending=pending.split(b'\n',1)
   try:records.append(json.loads(line.decode()))
   except (ValueError,UnicodeError):pass
 stats=[r['audio'] for r in records if 'audio' in r]
 result={'listeningReached':listening is not None,'elapsedSeconds':round(time.monotonic()-started,2),'audio':stats[-1] if stats else None,'voiceStates':[r['voice'] for r in records if 'voice' in r],'scope':'Computer synthesized speech through system speaker, actual board microphone, provider and speaker-driver path. Subjective audibility and semantic transcript require separate review.'}
 result['fatalDiagnostics']=[s for s in ('task_wdt','Guru Meditation','stack overflow','assert failed','audioFault','linkFault') if s.encode() in log]
 if a.manual:result['scope']='Human acoustic wake and voice trial; no computer stimulus or USB start. Transcript and user feedback require separate review.'
 result['audibleFirmwareConfirmed']=audible
 result['speakerVolume']=next((r['speakerVolume'] for r in reversed(records) if 'speakerVolume' in r),None)
 interrupts=[r['localInterrupts'] for r in records if 'localInterrupts' in r]
 result['localInterrupts']=interrupts[-1]-interrupts[0] if interrupts else 0
 suppressed=[r['echoSuppressedSamples16k'] for r in records if 'echoSuppressedSamples16k' in r]
 result['echoSuppressedSamples16k']=suppressed[-1]-suppressed[0] if suppressed else 0
 result['wakeOnlyActivation']=a.wake
 wakes=[r['afe']['wakeDetections'] for r in records if 'afe' in r]
 result['wakeDetections']=wakes[-1]-wakes[0] if wakes else 0
 result['wakeActivationRestoreRequestedAfterTest']=True
 result['prerollSamples16k']=next((r['prerollSamples16k'] for r in reversed(records) if 'prerollSamples16k' in r),0)
 result['playedSamplesThisRun']=stats[-1]['playedSamples16k']-stats[0]['playedSamples16k'] if stats else 0
 result['capturedSamplesThisRun']=stats[-1]['capturedSamples16k']-stats[0]['capturedSamples16k'] if stats else 0
 result['interruptRequested']=interrupted is not None
 result['samplesWrittenAfterInterruptRequest']=stats[-1]['playedSamples16k']-played_at_interrupt if interrupted and stats else None
 if a.interrupt:result['scope']+=' USB interruption measures driver submissions, not acoustic silence latency.'
 result['fixtureExitCode']=play.poll() if play else None
 result['acousticBargeFixturePlayed']=barge is not None
 result['bargeFixtureExitCode']=barge.poll() if barge else None
 result['playbackTiming']=next((r['playbackTiming'] for r in reversed(records) if 'playbackTiming' in r),None)
 result['afeTiming']=next((r['afeTiming'] for r in reversed(records) if 'afeTiming' in r),None)
 result['audioTiming']=next((r['audioTiming'] for r in reversed(records) if 'audioTiming' in r),None)
 result['linkFailureDetails']=[r for r in records if 'linkFault' in r]
 result['captureQueue']=next((r['captureQueue'] for r in reversed(records) if 'captureQueue' in r),None)
 result['uplink']=next((r['uplink'] for r in reversed(records) if 'uplink' in r),None)
 result['passed']=not result['fatalDiagnostics'] and audible and bool(listening and stats and result['playedSamplesThisRun']>0 and not stats[-1]['captureActive'])
 if not a.interrupt:result['passed']=result['passed'] and result['capturedSamplesThisRun']+result['echoSuppressedSamples16k']>=24*16000
 result['passed']=result['passed'] and (a.manual or result['fixtureExitCode']==0)
 if not a.manual and not a.barge_fixture and not a.interrupt:result['passed']=result['passed'] and result['localInterrupts']==0
 if a.barge_fixture:result['passed']=result['passed'] and result['localInterrupts']>0 and result['bargeFixtureExitCode']==0
 if a.wake:result['passed']=result['passed'] and result['wakeDetections']>0 and wake_confirmed
 if a.interrupt:result['passed']=result['passed'] and interrupted is not None and result['samplesWrittenAfterInterruptRequest']<=1600
except serial.SerialException:
 result={'passed':False,'serialDisconnected':True,'listeningReached':listening is not None,'elapsedSeconds':round(time.monotonic()-started,2),'scope':'Trial interrupted by serial transport loss; not a wake or acoustic pass.'}
finally:
 if port.is_open:
  try:port.write(b'xW')
  except serial.SerialException:pass
  port.close()
 if play and play.poll() is None:play.terminate()
 if barge and barge.poll() is None:barge.terminate()
 stamp=int(time.time()*1000);(root/f'work/board/body-audio-{stamp}.log').write_bytes(log);(root/f'work/board/body-audio-{stamp}.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result));raise SystemExit(0 if result.get('passed') else 1)
