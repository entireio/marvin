import numpy as np, sys
from PIL import Image
def lin(v):
    v=v/255.0; return np.where(v<=0.04045,v/12.92,((v+0.055)/1.055)**2.4)
d=sys.argv[1]
def prof(path, centers, ks):
    a=lin(np.asarray(Image.open(path).convert('RGB')).astype(float)); l=(a*[0.2126,0.7152,0.0722]).sum(-1)
    ys,xs=np.mgrid[0:a.shape[0],0:a.shape[1]]
    return np.array([[l[(np.hypot(xs-cx,ys-cy)>=k)&(np.hypot(xs-cx,ys-cy)<k+2)].mean() for k in ks] for cx,cy in centers])
ks=range(8,60,4)
s=prof('sk/race_evening_chase.png',[(450,76),(369,248)],ks); g=prof(f'{d}/race_evening_chase.png',[(450,76),(369,248)],ks)
e=np.sqrt(((s-g)**2).mean())
print(f"evening sun rms {e:.4f}  sunA d: "+' '.join(f"{v:+.3f}" for v in (g-s)[0][:8]))
