import json,sys,numpy as np
from PIL import Image
# probe.py NAME dir1 dir2 ... : prints luma at probes (and extra x,y points given as NAME:x,y via EXTRA env)
import os
def lin(v):
    v=v/255.0; return np.where(v<=0.04045,v/12.92,((v+0.055)/1.055)**2.4)
name=sys.argv[1]; dirs=sys.argv[2:]
pr=[(p['name'],int(p['x']),int(p['y'])) for p in json.load(open('sk/probes.json')).get(name,[])]
for e in os.environ.get('EXTRA','').split(';'):
    if e: n,xy=e.split(':'); x,y=xy.split(','); pr.append((n,int(x),int(y)))
imgs={d:lin(np.asarray(Image.open(f'{d}/{name}.png').convert('RGB')).astype(float)) for d in dirs}
print(f"{'probe':<16}"+''.join(f"{d:>14}" for d in dirs))
for n,x,y in pr:
    vals=[]
    for d in dirs:
        a=imgs[d]
        if not (2<=x<a.shape[1]-2 and 2<=y<a.shape[0]-2): vals.append(float('nan')); continue
        vals.append(float((a[y-2:y+3,x-2:x+3].reshape(-1,3)*[0.2126,0.7152,0.0722]).sum(1).mean()))
    print(f"{n:<16}"+''.join(f"{v:14.4f}" for v in vals))
