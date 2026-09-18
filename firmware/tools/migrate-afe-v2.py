#!/usr/bin/env python3
"""Migrate a verified Waveshare board from AFE layout v1 to v2.

This is deliberately separate from OTA. It copies the model out of the source
snapshot before writing its overlapping v2 destination and selects the new app
only after every image and the new partition table have been verified.
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from release_key import bootstrap_capsule, public_release_key

FLASH_BYTES=0x1000000
V1_MODEL=(0x3e0000,0x400000)
V2_MODEL=(0x620000,0x400000)
V1_SLOT0=(0x20000,0x1e0000)
V2_SLOT1=(0x320000,0x300000)

def sha(value):
    if isinstance(value,Path):
        with value.open('rb') as source:return hashlib.file_digest(source,'sha256').hexdigest()
    return hashlib.sha256(value).hexdigest()

def require_file(path, label):
    if not path.is_file():raise RuntimeError(f'Missing {label}: {path}')

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--snapshot',type=Path,required=True)
parser.add_argument('--prior-app',type=Path,required=True)
parser.add_argument('--prior-partition',type=Path,required=True)
parser.add_argument('--v2-build',type=Path,required=True)
parser.add_argument('--release',type=Path,required=True)
parser.add_argument('--public-key',type=Path,required=True)
parser.add_argument('--factory',type=Path,required=True)
parser.add_argument('--port',default='/dev/cu.usbmodem1101')
parser.add_argument('--flash',action='store_true')
args=parser.parse_args()
if not re.fullmatch(r'/dev/cu\.usbmodem[\w.-]+',args.port):parser.error('Select the Waveshare USB modem port')
manifest=json.loads((args.snapshot/'manifest.json').read_text())
snapshot=args.snapshot/'original-flash.bin'
for path,label in ((snapshot,'snapshot'),(args.prior_app,'prior app'),(args.prior_partition,'prior partition'),(args.factory,'factory image'),(args.v2_build/'marvin.bin','v2 app'),(args.v2_build/'bootloader/bootloader.bin','v2 bootloader'),(args.v2_build/'partition_table/partition-table.bin','v2 partition'),(args.release/'image.bin','signed image'),(args.release/'manifest.bin','signed manifest'),(args.release/'release.json','release metadata')):require_file(path,label)
if manifest.get('verifiedBySecondRead') is not True or manifest.get('bytes')!=FLASH_BYTES or snapshot.stat().st_size!=FLASH_BYTES or sha(snapshot)!=manifest.get('sha256'):raise RuntimeError('A fresh intact double-read 16 MB snapshot is required')
if args.factory.stat().st_size!=0x6000:raise RuntimeError('Factory image must be exactly 0x6000 bytes')
release=json.loads((args.release/'release.json').read_text())
candidate=(args.release/'image.bin').read_bytes();v2_app=(args.v2_build/'marvin.bin').read_bytes()
signed=(args.release/'manifest.bin').read_bytes()
if len(signed)!=176 or signed[:8]!=b'MRVOTA01' or signed[48:80].rstrip(b'\0')!=b'waveshare-esp32s3-audio' or signed[80:96].rstrip(b'\0')!=b'afe-v2' or signed[12:16]!=len(candidate).to_bytes(4,'big') or signed[16:48]!=hashlib.sha256(candidate).digest() or signed[96:112]!=bytes(16):raise RuntimeError('Release manifest does not bind this afe-v2 image')
if release.get('layout')!='afe-v2' or release.get('sequence')!=int.from_bytes(signed[8:12],'big') or candidate!=v2_app or not 1024<=len(candidate)<=V2_SLOT1[1] or candidate[:1]!=b'\xe9':raise RuntimeError('Release must be the reviewed signed afe-v2 build')
if bootstrap_capsule(args.release/'manifest.bin',public_release_key(args.public_key))!=signed:raise RuntimeError('Release signature is not trusted by the supplied public key')
config=(args.v2_build/'config/sdkconfig.h').read_text()
for setting in ('CONFIG_MARVIN_AFE_LAYOUT_V2','CONFIG_MARVIN_SIGNED_OTA','CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE','CONFIG_MARVIN_WAKE_AUTOSTART'):
    if f'#define {setting} 1' not in config:raise RuntimeError(f'v2 build lacks {setting}')
flash=snapshot.read_bytes();prior=args.prior_app.read_bytes();prior_partition=args.prior_partition.read_bytes();v2_partition=(args.v2_build/'partition_table/partition-table.bin').read_bytes()
if len(prior)>V1_SLOT0[1] or flash[V1_SLOT0[0]:V1_SLOT0[0]+len(prior)]!=prior:raise RuntimeError('Snapshot slot 0 differs from the attested prior app')
if flash[0x8000:0x8000+len(prior_partition)]!=prior_partition:raise RuntimeError('Snapshot partition table differs from supplied v1 partition')
if b'ota_1' not in v2_partition or len(v2_partition)>0x1000:raise RuntimeError('Unexpected v2 partition table')
model=flash[V1_MODEL[0]:V1_MODEL[0]+V1_MODEL[1]]
if model==bytes([0xff])*V1_MODEL[1]:raise RuntimeError('Snapshot has no source wake model')
plan={'operation':'migrate AFE v1 partition layout to afe-v2','port':args.port,'snapshotSha256':manifest['sha256'],'sourceModelSha256':sha(model),'candidateSha256':sha(candidate),'writesInOrder':{'0x620000': 'preserved v1 model copied from verified snapshot','0x320000':str((args.release/'image.bin').resolve()),'0x0':str((args.v2_build/'bootloader/bootloader.bin').resolve()),'0x12000':str(args.factory.resolve()),'0x8000':str((args.v2_build/'partition_table/partition-table.bin').resolve()),'0x10000-last':'new OTA selector'},'recovery':'Restore the verified snapshot with the guarded recovery process if migration is interrupted before healthy boot.'}
print(json.dumps(plan,indent=2),flush=True)
if not args.flash:raise SystemExit('Plan only. Add --flash after reviewing the plan and recovery snapshot.')
base=[sys.executable,'-m','esptool','--chip','esp32s3','--port',args.port,'--baud','460800']
def run(parts,timeout=300):subprocess.run(base+parts,check=True,timeout=timeout)
def write(offset,path):
    run(['--after','no_reset','write_flash','--flash_mode','dio','--flash_size','16MB','--flash_freq','80m',hex(offset),str(path)])
    # Do not reset between selection and the application's confirmation window.
    run(['--after','no_reset','verify_flash',hex(offset),str(path)],180)
model_path=args.release/'migrated-model.bin'
if model_path.exists() and model_path.read_bytes()!=model:raise RuntimeError('Existing staged model differs from snapshot')
if not model_path.exists():model_path.write_bytes(model)
write(V2_MODEL[0],model_path)
write(V2_SLOT1[0],args.release/'image.bin')
write(0,args.v2_build/'bootloader/bootloader.bin')
write(0x12000,args.factory)
write(0x8000,args.v2_build/'partition_table/partition-table.bin')
# Sequence 1 valid slot 0, sequence 2 NEW slot 1. Selector is deliberately last.
from ota_metadata import NEW,VALID,entry
selector=entry(1,VALID)+bytes([0xff])*(4096-32)+entry(2,NEW)+bytes([0xff])*(4096-32)
selector_path=args.release/'migrate-otadata.bin';selector_path.write_bytes(selector)
write(0x10000,selector_path)
run(['--after','hard_reset','chip_id'])
(args.release/'migration-result.json').write_text(json.dumps({**plan,'completedAt':datetime.now(timezone.utc).isoformat(),'flashVerified':True,'healthyBootConfirmationPending':True},indent=2)+'\n')
