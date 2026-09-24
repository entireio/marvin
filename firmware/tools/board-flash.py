#!/usr/bin/env python3
"""Install a Waveshare bench profile, after verifying a matching full backup."""
import argparse,hashlib,importlib.util,json,os,re,subprocess,sys,tempfile
from pathlib import Path
from datetime import datetime,timezone

# The macOS system Python does not contain esptool. When this helper is called
# directly, transparently re-run it with the bundled ESP-IDF environment so a
# failed Python-module discovery cannot interrupt an otherwise valid flash.
root=Path(__file__).resolve().parents[2]
if importlib.util.find_spec('esptool') is None and not os.environ.get('MARVIN_FLASH_PYTHON_REEXEC'):
    idf_python=root/'work/idf-tools/python_env/idf5.4_py3.13_env/bin/python'
    if idf_python.exists():
        os.environ['MARVIN_FLASH_PYTHON_REEXEC']='1'
        os.execv(str(idf_python),[str(idf_python),*sys.argv])

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--backup',type=Path,required=True)
p.add_argument('--port',default='/dev/cu.usbmodem1101')
p.add_argument('--profile',choices=['audio','provisioning','owner','local-dev','afe','afe-audible','motion-afe','release-v3'],default='audio')
p.add_argument('--factory',type=Path,help='Private generated factory directory; required by owner-capable profiles and verified, never written, with --reuse-verified-base')
p.add_argument('--reuse-verified-base',action='store_true',help='Verify the installed bootloader, partitions, model and factory against this build; write only OTA metadata and application')
p.add_argument('--app-only',action='store_true',help='Fast local-dev loop: write only the reviewed application partition, preserving factory, Wi-Fi and OTA metadata')
p.add_argument('--flash',action='store_true',help='Actually install the diagnostic; otherwise show the plan only')
a=p.parse_args()
suffix='' if a.profile=='audio' else '-'+a.profile;build=root/('build-waveshare'+suffix)
if not re.fullmatch(r'/dev/cu\.usbmodem[\w.-]+',a.port):p.error('Select the Waveshare USB modem port.')
manifest=json.loads((a.backup/'manifest.json').read_text());original=a.backup/'original-flash.bin'
sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
if manifest.get('verifiedBySecondRead') is not True or manifest.get('bytes')!=16777216 or original.stat().st_size!=16777216 or sha(original)!=manifest['sha256']:raise RuntimeError('A verified, intact 16 MB backup is required.')
config_name='sdkconfig.waveshare-motion.generated' if a.profile=='motion-afe' else 'sdkconfig.waveshare'+suffix+'.generated'
config=(root/'firmware'/config_name).read_text()
diagnostic='CONFIG_MARVIN_WAVESHARE_AUDIO_DIAGNOSTIC=y' in config
if diagnostic!=(a.profile=='audio'):raise RuntimeError('Build does not match selected profile.')
if a.profile!='audio' and not a.factory and not (a.profile=='local-dev' and a.app_only):p.error('--factory is required for provisioning')
if a.app_only and a.profile!='local-dev':p.error('--app-only is restricted to the explicit local-dev profile')
if a.app_only and a.reuse_verified_base:p.error('--app-only already preserves the base; do not combine it with the slower base verification')
if ('CONFIG_MARVIN_OWNER_ENROLLMENT=y' in config)!=(a.profile in ('owner','local-dev','afe','afe-audible','motion-afe','release-v3')):raise RuntimeError('Owner authorization does not match the selected profile.')
if ('CONFIG_MARVIN_LOCAL_DEV_MODE=y' in config)!=(a.profile=='local-dev'):raise RuntimeError('Local development mode does not match the selected profile.')
afe_profile=a.profile in ('local-dev','afe','afe-audible','motion-afe','release-v3')
if ('CONFIG_MARVIN_LOCAL_AFE=y' in config)!=afe_profile:raise RuntimeError('Local AFE configuration does not match the selected profile.')
if a.profile=='local-dev' and any('CONFIG_'+option+'=y' not in config for option in ('MARVIN_WAKE_AUTOSTART','MARVIN_MICRO_WAKE_WORD','MARVIN_HEY_MARVIN_WAKE')):raise RuntimeError('Local development firmware must retain the full wake path.')
if a.profile in ('afe-audible','motion-afe','release-v3') and ('CONFIG_MARVIN_SILENT_TEST=y' in config or 'CONFIG_MARVIN_SPEAKER_VOLUME=95' not in config):raise RuntimeError('Audible profile must have speaker enabled at volume 95.')
if a.profile=='motion-afe' and any('CONFIG_'+option+'=y' not in config for option in ('MARVIN_WAKE_AUTOSTART','MARVIN_MICRO_WAKE_WORD','MARVIN_HEY_MARVIN_WAKE')):raise RuntimeError('Motion profile is missing a required wake option.')
if a.profile=='release-v3' and any('CONFIG_'+option+'=y' not in config for option in ('MARVIN_SIGNED_OTA','BOOTLOADER_APP_ROLLBACK_ENABLE','MARVIN_WAKE_AUTOSTART','MARVIN_MICRO_WAKE_WORD','MARVIN_HEY_MARVIN_WAKE','MARVIN_AFE_LAYOUT_V3')):raise RuntimeError('Release-v3 profile is missing a required release option.')
args=json.loads((build/'flasher_args.json').read_text())
expected={'0x0':'bootloader/bootloader.bin','0x8000':'partition_table/partition-table.bin','0x10000':'ota_data_initial.bin','0x20000':'marvin.bin'}
afe_v3='CONFIG_MARVIN_AFE_LAYOUT_V3=y' in config
model_offset='0x820000' if afe_v3 else '0x3e0000'
if afe_profile:expected[model_offset]='srmodels/srmodels.bin'
if args['flash_files']!=expected or args['flash_settings']!={'flash_mode':'dio','flash_size':'16MB','flash_freq':'80m'}:raise RuntimeError('Unexpected image layout; stop for review.')
images={offset:{'path':str(build/name),'sha256':sha(build/name),'bytes':(build/name).stat().st_size} for offset,name in expected.items()}
if afe_profile and images[model_offset]['bytes']>0x400000:raise RuntimeError('AFE model exceeds its reviewed partition.')
if a.profile!='audio' and not a.app_only:
 factory=a.factory/'factory.bin'
 if factory.stat().st_size!=0x6000:raise RuntimeError('Unexpected factory partition size')
 images['0x12000']={'path':str(factory.resolve()),'sha256':sha(factory),'bytes':factory.stat().st_size}
factory_preserved=a.app_only or a.reuse_verified_base
ota_metadata_preserved=a.app_only
planned_images={k:v for k,v in images.items() if (a.app_only and k=='0x20000') or (a.reuse_verified_base and k in ('0x10000','0x20000')) or (not a.app_only and not a.reuse_verified_base)}
verified_base={k:v for k,v in images.items() if a.reuse_verified_base and k not in ('0x10000','0x20000')}
kind='release firmware' if a.profile=='release-v3' else 'development firmware'
print(json.dumps({'operation':'install '+a.profile+(' app-only development firmware' if a.app_only else ' '+kind),'backupVerified':True,'port':a.port,'imagesToWrite':planned_images,'verifiedBaseImages':verified_base,'factoryPreserved':factory_preserved,'otaMetadataPreserved':ota_metadata_preserved},indent=2),flush=True)
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
print('Installing selected firmware profile. No eFuses are programmed.',flush=True)
if a.reuse_verified_base:
 verify=base+['--after','no_reset','verify_flash']
 for offset,image in images.items():
  if offset not in ('0x10000','0x20000'):verify += [offset,image['path']]
 subprocess.run(verify,check=True,timeout=180)
if a.app_only:
 # A raw model-byte comparison alone is insufficient: an older partition table
 # can leave those bytes orphaned, which makes AFE startup fail even when the
 # model itself is present. Require the installed table before preserving it.
 table=Path(images['0x8000']['path'])
 with tempfile.NamedTemporaryFile() as current_table:
  subprocess.run(base+['--after','no_reset','read_flash','0x8000',hex(table.stat().st_size),current_table.name],check=True,timeout=60)
  if sha(Path(current_table.name))!=images['0x8000']['sha256']:
   raise RuntimeError('Installed partition table does not match local-dev AFE layout. Run a full local-dev install; app-only will not orphan the wake model.')
 # Application-only iterations must also retain the exact reviewed wake model.
 subprocess.run(base+['--after','no_reset','verify_flash',model_offset,images[model_offset]['path']],check=True,timeout=180)
command=base+['write_flash']+args['write_flash_args']
for offset,image in images.items():
 if a.app_only:
  if offset=='0x20000':command += [offset,image['path']]
 elif not a.reuse_verified_base or offset in ('0x10000','0x20000'):command += [offset,image['path']]
subprocess.run(command,check=True,timeout=300)
(root/('work/board/'+a.profile+'-flash.json')).write_text(json.dumps({'flashedAt':datetime.now(timezone.utc).isoformat(),'backup':str(a.backup.resolve()),'imagesWritten':planned_images,'baseVerified':a.reuse_verified_base,'appOnly':a.app_only,'factoryPreserved':factory_preserved,'otaMetadataPreserved':ota_metadata_preserved,'esptoolWriteAndVerifySucceeded':True},indent=2)+'\n')
print('Firmware installed and verified: '+a.profile)
