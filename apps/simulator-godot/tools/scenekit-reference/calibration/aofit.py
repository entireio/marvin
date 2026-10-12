import json,sys,numpy as np
from PIL import Image
def lin(v):
    v=v/255.0; return np.where(v<=0.04045,v/12.92,((v+0.055)/1.055)**2.4)
name=sys.argv[1]; gd=sys.argv[2]
pts={'plaster_side':(900,430),'teal_front':(770,380),'house_base':(220,300),'house_front':(220,230),'banner':(478,340),'sph_m0_r0':(291,410),'sph_m0_r1':(358,410),'sph_m1_r1':(333,429),'ground_by_house':(230,330),'table_under':(1060,520),'colored_box':(774,347),'sph_m1_r4':(552,431)}
def L(d):
    a=lin(np.asarray(Image.open(f'{d}/{name}.png').convert('RGB')).astype(float))
    return {k:float((a[y-2:y+3,x-2:x+3].reshape(-1,3)*[0.2126,0.7152,0.0722]).sum(1).mean()) for k,(x,y) in pts.items()}
sk,skn,g,gn=L('sk'),L('sk_nossao'),L(gd),L('gd_nossao')
err=0
out=[]
for k in pts:
    fs=sk[k]/skn[k]; fg=g[k]/gn[k]; err+=(fg-fs)**2; out.append(f"{k}:{fs:.2f}/{fg:.2f}")
print(f"rms {np.sqrt(err/len(pts)):.3f}  "+"  ".join(out))
# whole image: mean luma darkening
a=lin(np.asarray(Image.open(f'sk/{name}.png').convert('RGB')).astype(float)).mean(); b=lin(np.asarray(Image.open(f'sk_nossao/{name}.png').convert('RGB')).astype(float)).mean()
c=lin(np.asarray(Image.open(f'{gd}/{name}.png').convert('RGB')).astype(float)).mean(); e=lin(np.asarray(Image.open(f'gd_nossao/{name}.png').convert('RGB')).astype(float)).mean()
print(f"  image mean darkening SK {a/b:.4f} GD {c/e:.4f}")
