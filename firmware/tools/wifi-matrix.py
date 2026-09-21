"""Measure ten real encrypted scan/connect cycles. Keep SSIDs and credentials private."""
import argparse,json,subprocess,time,hashlib
from pathlib import Path
from datetime import datetime,timezone
import serial
from esptool.reset import HardReset
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--factory',required=True);p.add_argument('--wifi-file',required=True)
a=p.parse_args();root=Path(__file__).resolve().parents[2]
folder=root/'work/board'/('wifi-matrix-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'));folder.mkdir(mode=0o700)
results=[]
for i in range(10):
 port=serial.Serial(port=None,baudrate=115200,timeout=.1);port.dtr=False;port.rts=False;port.port='/dev/cu.usbmodem1101';port.open()
 try:HardReset(port,uses_usb=True)()
 finally:port.close()
 start=time.monotonic()
 with (folder/f'trial-{i+1}.log').open('w') as log:
  try:r=subprocess.run(['node','--import','tsx','scripts/ble-smoke.ts',a.factory,'--wifi-file',a.wifi_file,'--verify-saved'],cwd=root,stdout=log,stderr=subprocess.STDOUT,timeout=120);passed=r.returncode==0
  except subprocess.TimeoutExpired:passed=False
 row={'trial':i+1,'passed':passed,'totalElapsedMs':round((time.monotonic()-start)*1000)}
 if passed:
  detail=json.loads((root/'work/board/wifi-smoke.json').read_text());row['applyElapsedMs']=detail['elapsedMs'];row['networkConnected']=detail['networkConnected']
 results.append(row)
 report={'at':datetime.now(timezone.utc).isoformat(),'trials':results,'complete':len(results)==10,'passed':len(results)==10 and all(r['passed'] for r in results),'firmwareSha256':hashlib.sha256((root/'build-waveshare-provisioning/marvin.bin').read_bytes()).hexdigest(),'scope':'Real Security2 identity proof, robot radio scan, Wi-Fi apply, certificate-validated LAN HTTPS probe and persistence across resets. Same test AP; no owner enrollment or different-location network test.'}
 (folder/'results.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(row),flush=True)
 if not passed:raise RuntimeError('Failed trial retained in '+str(folder))
print('Wi-Fi matrix completed: '+str(folder),flush=True)
