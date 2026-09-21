"""Silent capture/session regression; never generates or plays a sound.

Requires a flashed MARVIN_SILENT_TEST build. Records numeric diagnostics only;
the explicitly activated microphone streams briefly to the configured provider.
"""
import json
import os
import time
from pathlib import Path
import serial

os.umask(0o077)
root = Path(__file__).resolve().parents[2]
config = (root / 'firmware/sdkconfig.waveshare-afe.generated').read_text()
if '\nCONFIG_MARVIN_SILENT_TEST=y\n' not in '\n' + config:
    raise SystemExit('Refusing: owner build is not configured for silent testing')
port = serial.Serial(port=None, baudrate=115200, timeout=.1, write_timeout=1)
port.dtr = False
port.rts = False
port.port = '/dev/cu.usbmodem1101'
records, log, pending = [], bytearray(), b''
started = time.monotonic()
listening = stopped = None
silent_confirmed = False
last_request = last_status = 0
result = {}
try:
    port.open()
    while time.monotonic() - started < 100:
        now = time.monotonic()
        if silent_confirmed and listening is None and now - started > 15 and now - last_request > 5:
            port.write(b'v')
            last_request = now
        if now - last_status > 5:
            port.write(b's')
            last_status = now
        if listening and stopped is None and now - listening > 30:
            port.write(b'xs')
            stopped = now
        data = port.read(4096)
        log.extend(data)
        pending += data
        while b'\n' in pending:
            line, pending = pending.split(b'\n', 1)
            try:
                record = json.loads(line.decode())
                records.append(record)
                if record.get("audio", {}).get("silent") is True:
                    silent_confirmed = True
                if record.get('voice') == 'listening' and listening is None:
                    listening = now
                if listening and stopped is None and ('audioFault' in record or record.get('voice') == 'closed'):
                    port.write(b'xs')
                    stopped = now
            except (ValueError, UnicodeError):
                pass
        if stopped and now - stopped > 10:
            break
    stats = [r['audio'] for r in records if 'audio' in r]
    final = stats[-1] if stats else {}
    errors = [s for s in ('task_wdt', 'Guru Meditation', 'Stack canary watchpoint', 'stack overflow', 'assert failed', 'audioFault') if s.encode() in log]
    result = {
        'listeningReached': listening is not None,
        'elapsedSeconds': round(time.monotonic() - started, 2),
        'audio': final,
        'voiceStates': [r['voice'] for r in records if 'voice' in r],
        'captureQueue': next((r['captureQueue'] for r in reversed(records) if 'captureQueue' in r), None),
        'uplink': next((r['uplink'] for r in reversed(records) if 'uplink' in r), None),
        'fatalDiagnostics': errors,
        'scope': 'Silent amplifier-disabled physical microphone capture and live provider session, followed by local stop. No speech or acoustic playback stimulus; not an audio quality/latency test.',
        'passed': bool(listening and final.get('capturedSamples16k', 0) >= 16000 * 28 and not final.get('captureActive', True) and not errors),
    }
    result['passed'] = result['passed'] and (result['uplink'] or {}).get('pcmSamples16k', 0) >= 16000*28
finally:
    if port.is_open:
        try:
            port.write(b'x')
        except serial.SerialException:
            pass
        port.close()
    stamp = int(time.time() * 1000)
    (root / f'work/board/body-silent-{stamp}.log').write_bytes(log)
    (root / f'work/board/body-silent-{stamp}.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result))
raise SystemExit(0 if result.get('passed') else 1)
