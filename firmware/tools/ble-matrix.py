"""Repeat real TypeScript-to-firmware Security2 scans without applying Wi-Fi."""
import json,subprocess,time
from pathlib import Path
from datetime import datetime,timezone
import serial
from esptool.reset import HardReset
root=Path(__file__).resolve().parents[2]
folder=root/'work/board'/('ble-matrix-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'));folder.mkdir(mode=0o700)
results=[]
for i in range(11):
 with serial.Serial('/dev/cu.usbmodem1101',115200,timeout=.1) as port:
  port.dtr=False;HardReset(port,uses_usb=True)()
 start=time.monotonic();negative=i==10
 with (folder/f'trial-{i+1}.txt').open('w') as log:
  result=subprocess.run(['node','--import','tsx','scripts/ble-smoke.ts','work/board/factory-288485b2ab10']+(['--bad-proof'] if negative else []),cwd=root,stdout=log,stderr=subprocess.STDOUT,timeout=65)
 row={'trial':i+1,'kind':'wrong-setup-secret' if negative else 'secure-radio-scan','passed':result.returncode==0,'elapsedMs':round((time.monotonic()-start)*1000)}
 results.append(row);(folder/'results.json').write_text(json.dumps({'trials':results,'complete':i==10,'scope':'Encrypted scan and identity only; no Wi-Fi apply or account claim'},indent=2)+'\n');print(json.dumps(row),flush=True)
 if result.returncode:raise RuntimeError('Trial failed; inspect '+str(folder))
print('BLE matrix completed: '+str(folder),flush=True)
