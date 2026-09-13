#!/usr/bin/env python3
"""Read-only flash backup and identification. Never flashes, erases or burns eFuses."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
from datetime import datetime, timezone


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', default='/dev/cu.usbmodem1101')
    args = parser.parse_args()
    if not re.fullmatch(r'/dev/cu\.usbmodem[\w.-]+', args.port):
        parser.error('Select the USB modem port belonging to the Waveshare board.')
    os.umask(0o077)
    root = Path(__file__).resolve().parents[2]
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    dest = root / 'work' / 'board' / ('backup-' + stamp)
    dest.mkdir(parents=True, exist_ok=False)
    base = [sys.executable, '-m', 'esptool', '--chip', 'esp32s3', '--port', args.port, '--baud', '460800']
    def run(command, log):
        print(f"Starting {command[0]} — log: {dest / log}", flush=True)
        # Stream progress immediately, and retain partial output if interrupted.
        child = subprocess.Popen(base + command, stdout=subprocess.PIPE,
                                 stderr=subprocess.STDOUT, bufsize=0,
                                 env={**os.environ, 'PYTHONUNBUFFERED': '1'})
        timed_out = threading.Event()
        def timeout():
            timed_out.set()
            child.kill()
        timer = threading.Timer(600, timeout)
        timer.daemon = True
        timer.start()
        chunks = []
        try:
            with (dest / log).open('wb') as output:
                # esptool v4 read_flash uses backspaces, NOT line endings.
                # Reading lines hides every progress update until completion.
                while True:
                    chunk = os.read(child.stdout.fileno(), 4096)
                    if not chunk:
                        break
                    output.write(chunk)
                    output.flush()
                    chunks.append(chunk)
                    sys.stdout.buffer.write(chunk)
                    sys.stdout.buffer.flush()
            code = child.wait()
        except BaseException:
            child.terminate()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()
            raise
        finally:
            timer.cancel()
            child.stdout.close()
        if timed_out.is_set():
            raise RuntimeError(f'{command[0]} timed out after 10 minutes. See {dest / log}. No flash was written.')
        if code:
            raise RuntimeError(f'{command[0]} failed. See {dest / log}. No flash was written.')
        print(f"Finished {command[0]}", flush=True)
        return b''.join(chunks).decode('utf-8', errors='replace')
    chip = run(['chip_id'], 'chip.txt')
    flash = run(['flash_id'], 'flash.txt')
    run(['get_security_info'], 'security.txt')
    if 'ESP32-S3' not in chip or '16MB' not in flash:
        raise RuntimeError('Chip or flash capacity differs from this board profile. Stop for review.')
    # Keep ROM loader running across both reads: existing firmware cannot mutate NVS.
    base += ['--after', 'no_reset']
    first = dest / 'original-flash.bin'
    second = dest / 'verification-flash.bin'
    run(['read_flash', '0', '0x1000000', str(first)], 'read.txt')
    run(['read_flash', '0', '0x1000000', str(second)], 'verify-read.txt')
    digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
    if first.stat().st_size != 16 * 1024 * 1024 or digest(first) != digest(second):
        raise RuntimeError('The two flash reads differ. Preserve both files and stop for review.')
    second.unlink()
    (dest / 'manifest.json').write_text(json.dumps({'board':'Waveshare ESP32-S3-AUDIO-Board','port':args.port,'capturedAt':stamp,'bytes':first.stat().st_size,'sha256':digest(first),'verifiedBySecondRead':True,'flashed':False}, indent=2) + '\n')
    print(f'Backup verified: {dest}\nBoard remains in ROM loader; reset it to resume its original firmware.\nNo flash, NVS, or eFuses were written. Keep the backup private.')

if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('Backup interrupted. No flash was written. Partial logs were preserved.', file=sys.stderr)
        sys.exit(130)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
