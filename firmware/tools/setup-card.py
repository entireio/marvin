"""Create a private copyable setup code from an existing factory secret. Never prints the code."""
import argparse,base64,json,os,re
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--factory',type=Path,required=True);a=p.parse_args()
source=json.loads((a.factory/'setup-secret.json').read_text())
if source.get('security')!=2 or source.get('patch')!=1 or not re.fullmatch(r'marvin_[a-f0-9]{32}',source.get('deviceId','')):raise RuntimeError('Unsupported setup credential format')
card={k:source[k] for k in ['deviceId','username','password']}
code='marvin1.'+base64.urlsafe_b64encode(json.dumps(card,separators=(',',':')).encode()).decode().rstrip('=')+'\n'
with open(a.factory/'setup-card.txt','x',opener=lambda path,flags:os.open(path,flags,0o600)) as f:f.write(code)
print('Private setup-card.txt created. Paste its code only into your trusted Marvin portal.')
