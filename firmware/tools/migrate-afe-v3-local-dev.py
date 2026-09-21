#!/usr/bin/env python3
"""Migrate a verified AFE v1 development board to the 4 MiB-slot AFE v3 map.

The model and candidate application are written and verified before the
partition table is changed. Runtime NVS, factory identity, PHY data and OTA
selection metadata are never written. No eFuses are changed.
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

FLASH_BYTES = 0x1000000
V1_MODEL = (0x3E0000, 0x400000)
V3_MODEL = (0x820000, 0x400000)
V3_SLOT0 = (0x20000, 0x400000)

def sha(value):
    if isinstance(value, Path):
        with value.open('rb') as source:
            return hashlib.file_digest(source, 'sha256').hexdigest()
    return hashlib.sha256(value).hexdigest()

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--snapshot', type=Path, required=True)
parser.add_argument('--v3-build', type=Path, required=True)
parser.add_argument('--port', default='/dev/cu.usbmodem101')
parser.add_argument('--flash', action='store_true')
args = parser.parse_args()
if not re.fullmatch(r'/dev/cu\.usbmodem[\w.-]+', args.port):
    parser.error('Select the Waveshare USB modem port')

manifest = json.loads((args.snapshot / 'manifest.json').read_text())
snapshot = args.snapshot / 'original-flash.bin'
candidate = args.v3_build / 'marvin.bin'
partition = args.v3_build / 'partition_table/partition-table.bin'
config = args.v3_build / 'config/sdkconfig.h'
for path, label in ((snapshot, 'snapshot'), (candidate, 'v3 app'),
                    (partition, 'v3 partition table'), (config, 'v3 config')):
    if not path.is_file():
        raise RuntimeError(f'Missing {label}: {path}')
if manifest.get('verifiedBySecondRead') is not True or manifest.get('bytes') != FLASH_BYTES or snapshot.stat().st_size != FLASH_BYTES or sha(snapshot) != manifest.get('sha256'):
    raise RuntimeError('A fresh intact double-read 16 MB snapshot is required')
image = candidate.read_bytes()
if not 1024 <= len(image) <= V3_SLOT0[1] or image[:1] != b'\xe9':
    raise RuntimeError('Candidate is not an ESP32-S3 image that fits the v3 slot')
settings = config.read_text()
for setting in ('CONFIG_MARVIN_AFE_LAYOUT_V3', 'CONFIG_MARVIN_LOCAL_DEV_MODE'):
    if f'#define {setting} 1' not in settings:
        raise RuntimeError(f'Build lacks {setting}')
if '#define CONFIG_MARVIN_SIGNED_OTA 1' in settings:
    raise RuntimeError('This migration tool accepts only the local development profile')
old_table = snapshot.read_bytes()[0x8000:0x9000]
old_csv = subprocess.run([sys.executable, str(Path(__file__).resolve().parents[2] / 'work/esp-idf/components/partition_table/gen_esp32part.py'), '/dev/stdin'], input=old_table, capture_output=True).stdout
for expected in (b'ota_0,app,ota_0,0x20000,1920K', b'ota_1,app,ota_1,0x200000,1920K', b'model,data,spiffs,0x3e0000,4M'):
    if expected not in old_csv:
        raise RuntimeError('Snapshot is not the reviewed AFE v1 layout')
flash = snapshot.read_bytes()
model = flash[V1_MODEL[0]:V1_MODEL[0] + V1_MODEL[1]]
if model == bytes([0xFF]) * V1_MODEL[1]:
    raise RuntimeError('Snapshot has no source wake model')
partition_bytes = partition.read_bytes()
if len(partition_bytes) > 0x1000 or b'ota_1' not in partition_bytes:
    raise RuntimeError('Unexpected v3 partition table')

stage = args.snapshot / 'afe-v3-migration'
stage.mkdir(mode=0o700, exist_ok=True)
model_path = stage / 'model.bin'
if model_path.exists() and model_path.read_bytes() != model:
    raise RuntimeError('Existing staged model differs from snapshot')
if not model_path.exists():
    model_path.write_bytes(model)
plan = {
    'operation': 'migrate AFE v1 local development layout to afe-v3',
    'port': args.port,
    'snapshotSha256': manifest['sha256'],
    'sourceModelSha256': sha(model),
    'candidateSha256': sha(candidate),
    'slotBytes': V3_SLOT0[1],
    'freeBytes': V3_SLOT0[1] - len(image),
    'freePercent': round((V3_SLOT0[1] - len(image)) * 100 / V3_SLOT0[1], 2),
    'writesInOrder': {
        '0x820000': 'preserved wake model copied from verified snapshot',
        '0x20000': str(candidate.resolve()),
        '0x8000-last': str(partition.resolve()),
    },
    'preserved': ['NVS', 'PHY', 'OTA metadata', 'factory identity'],
    'recovery': f'Restore {snapshot.resolve()} if migration is interrupted.',
}
print(json.dumps(plan, indent=2), flush=True)
if not args.flash:
    raise SystemExit('Plan only. Add --flash after reviewing the plan and recovery snapshot.')

base = [sys.executable, '-m', 'esptool', '--chip', 'esp32s3', '--port', args.port, '--baud', '460800']
def run(parts, timeout=300):
    subprocess.run(base + parts, check=True, timeout=timeout)
def write(offset, path):
    common = ['--after', 'no_reset']
    run(common + ['write_flash', '--flash_mode', 'dio', '--flash_size', '16MB', '--flash_freq', '80m', hex(offset), str(path)])
    run(common + ['verify_flash', hex(offset), str(path)], 180)

write(V3_MODEL[0], model_path)
write(V3_SLOT0[0], candidate)
write(0x8000, partition)
run(['--after', 'hard_reset', 'chip_id'])
result = {**plan, 'completedAt': datetime.now(timezone.utc).isoformat(), 'flashVerified': True}
(stage / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
print('AFE v3 migration written and verified; board reset for USB boot observation.')
