"""Physical idle-runtime observation. Never activates voice or plays audio."""
import argparse
import json
import os
import time
from pathlib import Path
import serial

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--seconds', type=int, default=90)
p.add_argument('--interval', type=int, default=5)
p.add_argument('--mode', choices=('silent', 'release-suppressed'), default='silent')
a = p.parse_args()
if not 30 <= a.seconds <= 86400 or not 1 <= a.interval <= 60:
    p.error('Use 30–86400 seconds and a 1–60 second observation interval.')
os.umask(0o077)
root = Path(__file__).resolve().parents[2]
stamp = int(time.time() * 1000)
destination = root / f'work/board/runtime-soak-{stamp}.json'
port = serial.Serial(port=None, baudrate=115200, timeout=.2, write_timeout=1)
port.dtr = port.rts = False
port.port = '/dev/cu.usbmodem1101'
started = time.monotonic()
pending = b''
samples = []
sample_count = 0
all_idle = True
minimum_internal = None
afe_baseline = None
afe_last = None
uplink_baseline = None
uplink_last = None
faults = set()
last_status = 0
report = {}
try:
    port.open()
    if a.mode == 'release-suppressed':
        port.write(b'w')
    with (root / f'work/board/runtime-soak-{stamp}.log').open('wb') as log, (root / f'work/board/runtime-soak-{stamp}.jsonl').open('w') as observations:
        while time.monotonic() - started < a.seconds:
            now = time.monotonic()
            if now - last_status > a.interval:
                port.write(b'ws' if a.mode == 'release-suppressed' else b'xs')
                last_status = now
            data = port.read(4096)
            log.write(data)
            log.flush()
            pending += data
            changed = False
            while b'\n' in pending:
                line, pending = pending.split(b'\n', 1)
                for marker in (b'task_wdt', b'Guru Meditation', b'stack overflow', b'assert failed', b'audioFault', b'abort() was called'):
                    if marker in line:
                        faults.add(marker.decode())
                try:
                    value = json.loads(line.decode())
                    if 'audio' in value or 'afe' in value or 'uplink' in value:
                        observation = {'elapsedSeconds': round(now-started, 2), **value}
                        observations.write(json.dumps(observation)+'\n')
                        observations.flush()
                        samples.append(observation)
                        samples = samples[-4:]
                        sample_count += 1
                        changed = True
                        if 'audio' in value:
                            v = value['audio']
                            expected_silent = v.get('silent') is (a.mode == 'silent')
                            all_idle = all_idle and expected_silent and not v['captureActive'] and not v['playing']
                            minimum_internal = min(minimum_internal if minimum_internal is not None else v['internalFreeBytes'], v['internalFreeBytes'])
                        if 'afe' in value:
                            afe_last = observation
                            if now-started >= 30 and afe_baseline is None:
                                afe_baseline = observation
                            if a.mode == 'release-suppressed':
                                all_idle = all_idle and value['afe'].get('wakeActivationDisabled') is True
                        if 'uplink' in value:
                            uplink_last = observation
                            if uplink_baseline is None:
                                uplink_baseline = observation
                except (ValueError, UnicodeError):
                    pass
            audio = [s['audio'] for s in samples if 'audio' in s]
            afe = [s['afe'] for s in samples if 'afe' in s]
            uplink = [s['uplink'] for s in samples if 'uplink' in s]
            report = {'requestedSeconds': a.seconds, 'elapsedSeconds': round(now-started, 2), 'complete': False,
                      'fatalDiagnostics': sorted(faults), 'audio': audio[-1] if audio else None,
                      'afe': afe[-1] if afe else None, 'uplink': uplink[-1] if uplink else None,
                      'sampleCount': sample_count, 'mode': a.mode, 'allObservationsIdle': all_idle, 'minimumInternalFreeBytes': minimum_internal,
                      'scope': ('Physical idle runtime with PA disabled.' if a.mode == 'silent' else 'Physical audible release runtime with wake activation explicitly suppressed.') + ' Numeric diagnostics only. No acoustic stimulus or external power measurement.'}
            if changed:
                destination.write_text(json.dumps(report, indent=2)+'\n')
    report['complete'] = True
    report['idleSafetyPassed'] = bool(audio and not faults and all_idle)
    report['uplinkIdlePassed'] = False
    if uplink_baseline and uplink_last:
        first = uplink_baseline['uplink']
        last = uplink_last['uplink']
        report['uplinkPcmSamplesDelta'] = int(last.get('pcmSamples16k', 0)) - int(first.get('pcmSamples16k', 0))
        report['uplinkAttemptsDelta'] = int(last.get('attempts', 0)) - int(first.get('attempts', 0))
        report['uplinkIdlePassed'] = report['uplinkPcmSamplesDelta'] == 0 and report['uplinkAttemptsDelta'] == 0
    report['processingPassed'] = None
    if afe_last:
        interval = afe_last['elapsedSeconds'] - afe_baseline['elapsedSeconds'] if afe_baseline else 0
        rate = (afe_last['afe']['processedSamples16k']-afe_baseline['afe']['processedSamples16k']) / interval if interval else 0
        report['processingSamplesPerSecondAfterWarmup'] = round(rate, 2)
        report['processingPassed'] = bool(interval >= 30 and 15500 <= rate <= 16500 and afe_last['afe']['feedFaults'] == afe_baseline['afe']['feedFaults'])
    report['passed'] = report['idleSafetyPassed'] and report['uplinkIdlePassed'] and report['processingPassed'] is not False
finally:
    if port.is_open:
        try:
            port.write(b'x')
        except serial.SerialException:
            pass
        port.close()
    destination.write_text(json.dumps(report, indent=2)+'\n')
print(json.dumps({k:v for k,v in report.items() if k != 'samples'}))
raise SystemExit(0 if report.get('passed') else 1)
