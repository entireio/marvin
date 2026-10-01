"""Fail clearly on missing city assets/LFS pointers; update only after art QA."""
from pathlib import Path
import hashlib,json,sys,subprocess
root=Path(__file__).resolve().parents[2]
folder=root/'apps/simulator-macos/Resources/City'
manifest=folder/'manifest.json'
names=[f'{kind}-{channel}.jpg' for kind in ['plaster','cloth','metal'] for channel in ['base','normal','rough']]+['crowd.json','sky.hdr']
hashes={}
for name in names:
 path=folder/name
 if not path.exists() or path.stat().st_size<1024:
  sys.exit(f'City asset missing or Git LFS pointer: {path}. Run git lfs pull.')
 hashes[name]=hashlib.sha256(path.read_bytes()).hexdigest()
if '--update' in sys.argv:
 manifest.write_text(json.dumps(hashes,indent=2,sort_keys=True)+'\n')
elif hashes!=json.loads(manifest.read_text()):
 sys.exit('City asset hashes changed. Re-export/inspect the art, then run scripts/city/check-assets.py --update.')
subprocess.run([sys.executable,str(Path(__file__).with_name('check-crowd.py'))],check=True)
print('City assets verified')
