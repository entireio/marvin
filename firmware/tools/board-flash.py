#!/usr/bin/env python3
"""Install a Waveshare bench profile, after verifying a matching full backup."""
import argparse,hashlib,json,re,subprocess,sys
from pathlib import Path
from datetime import datetime,timezone
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--backup',type=Path,required=True)
p.add_argument('--port',default='/dev/cu.usbmodem1101')
p.add_argument('--profile',choices=['audio','provisioning','owner','afe','afe-audible','motion-afe'],default='audio')
p.add_argument('--factory',type=Path,help='Private generated factory directory, required for provisioning')
p.add_argument('--reuse-verified-base',action='store_true',help='Verify the unchanged bootloader, partitions, model and factory on the board; write only application and boot selector')
p.add_argument('--flash',action='store_true',help='Actually install the diagnostic; otherwise show the plan only')
a=p.parse_args()
root=Path(__file__).resolve().parents[2];suffix='' if a.profile=='audio' else '-'+a.profile;build=root/('build-waveshare'+suffix)
if not re.fullmatch(r'/dev/cu\.usbmodem[\w.-]+',a.port):p.error('Select the Waveshare USB modem port.')
manifest=json.loads((a.backup/'manifest.json').read_text());original=a.backup/'original-flash.bin'
sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
if manifest.get('verifiedBySecondRead') is not True or manifest.get('bytes')!=16777216 or original.stat().st_size!=16777216 or sha(original)!=manifest['sha256']:raise RuntimeError('A verified, intact 16 MB backup is required.')
config_name='sdkconfig.waveshare-motion.generated' if a.profile=='motion-afe' else 'sdkconfig.waveshare'+suffix+'.generated'
config=(root/'firmware'/config_name).read_text()
diagnostic='CONFIG_MARVIN_WAVESHARE_AUDIO_DIAGNOSTIC=y' in config
if diagnostic!=(a.profile=='audio'):raise RuntimeError('Build does not match selected profile.')
if a.profile!='audio' and not a.factory:p.error('--factory is required for provisioning')
if ('CONFIG_MARVIN_OWNER_ENROLLMENT=y' in config)!=(a.profile in ('owner','afe','afe-audible','motion-afe')):raise RuntimeError('Owner authorization does not match the selected profile.')
if ('CONFIG_MARVIN_LOCAL_AFE=y' in config)!=(a.profile in ('afe','afe-audible','motion-afe')):raise RuntimeError('Local AFE configuration does not match the selected profile.')
if a.profile in ('afe-audible','motion-afe') and ('CONFIG_MARVIN_SILENT_TEST=y' in config or 'CONFIG_MARVIN_SPEAKER_VOLUME=95' not in config):raise RuntimeError('Audible bench profile must have speaker enabled at volume 95.')
if a.profile=='motion-afe' and any('CONFIG_'+option+'=y' not in config for option in ('MARVIN_WAKE_AUTOSTART','MARVIN_MICRO_WAKE_WORD','MARVIN_HEY_MARVIN_WAKE','MARVIN_TRACK_BENCH_MODE')):raise RuntimeError('Motion profile is missing a required wake or track option.')
args=json.loads((build/'flasher_args.json').read_text())
expected={'0x0':'bootloader/bootloader.bin','0x8000':'partition_table/partition-table.bin','0x10000':'ota_data_initial.bin','0x20000':'marvin.bin'}
if a.profile in ('afe','afe-audible','motion-afe'):expected['0x3e0000']='srmodels/srmodels.bin'
if args['flash_files']!=expected or args['flash_settings']!={'flash_mode':'dio','flash_size':'16MB','flash_freq':'80m'}:raise RuntimeError('Unexpected image layout; stop for review.')
images={offset:{'path':str(build/name),'sha256':sha(build/name),'bytes':(build/name).stat().st_size} for offset,name in expected.items()}
if a.profile in ('afe','afe-audible','motion-afe') and images['0x3e0000']['bytes']>0x400000:raise RuntimeError('AFE model exceeds its reviewed partition.')
if a.profile!='audio':
 factory=a.factory/'factory.bin'
 if factory.stat().st_size!=0x6000:raise RuntimeError('Unexpected factory partition size')
 images['0x12000']={'path':str(factory.resolve()),'sha256':sha(factory),'bytes':factory.stat().st_size}
print(json.dumps({'operation':'install '+a.profile+' development firmware','backupVerified':True,'port':a.port,'images':images},indent=2),flush=True)
if not a.flash:
 print('Plan only. Add --flash to install. No serial access or writes were performed.');sys.exit(0)
base=[sys.executable,'-m','esptool','--chip','esp32s3','--port',a.port,'--baud','460800']
def inspect(command):
 result=subprocess.run(base+['--after','no_reset',command],capture_output=True,text=True,timeout=60)
 if result.returncode:raise RuntimeError(result.stdout+result.stderr)
 return result.stdout
current=inspect('chip_id');old=(a.backup/'chip.txt').read_text()
mac=lambda text:re.search(r'MAC: ([0-9a-f:]{17})',text,re.I)
if not mac(current) or not mac(old) or mac(current)[1].lower()!=mac(old)[1].lower():raise RuntimeError('Connected board does not match this backup.')
security=inspect('get_security_info')
if 'Secure Boot: Disabled' not in security or 'Flash Encryption: Disabled' not in security:raise RuntimeError('Board security state requires a different flashing procedure. No eFuses will be changed.')
if 'Detected flash size: 16MB' not in inspect('flash_id'):raise RuntimeError('Unexpected flash size.')
for image in images.values():
 if sha(Path(image['path']))!=image['sha256']:raise RuntimeError('An image changed after planning. Wait for the build to finish and rerun flashing.')
print('Installing selected development profile. No eFuses are programmed.',flush=True)
if a.reuse_verified_base:
 verify=base+['verify_flash']
 for offset,image in images.items():
  if offset not in ('0x10000','0x20000'):verify += [offset,image['path']]
 subprocess.run(verify,check=True,timeout=180)
command=base+['write_flash']+args['write_flash_args']
for offset,image in images.items():
 if not a.reuse_verified_base or offset in ('0x10000','0x20000'):command += [offset,image['path']]
subprocess.run(command,check=True,timeout=300)
(root/('work/board/'+a.profile+'-flash.json')).write_text(json.dumps({'flashedAt':datetime.now(timezone.utc).isoformat(),'backup':str(a.backup.resolve()),'images':images,'esptoolWriteAndVerifySucceeded':True},indent=2)+'\n')
print('Development firmware installed and verified: '+a.profile)
