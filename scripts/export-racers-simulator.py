#!/usr/bin/env python3
"""Convert attributed BB-8/WALL-E assets offline, retaining articulated parts.
WALL-E's authored belt cycle is retimed by signed track travel in the simulator.
"""
from racer_gltf import Model, vec, mul
import hashlib,json,math
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]/'apps/simulator-macos/Resources'
I=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]
def inverse_rigid(a):
 # Uniform-scale affine transform, as present in these two pinned assets.
 scale2=sum(a[i]**2 for i in [0,1,2])
 r=[a[row*4+col]/scale2 if row<3 and col<3 else (1 if row==col else 0) for col in range(4) for row in range(4)]
 r[12:15]=vec(r,[-a[12],-a[13],-a[14]],0)
 return r
def bounds(ps):return ([min(p[i] for p in ps) for i in range(3)],[max(p[i] for p in ps) for i in range(3)])
def middle(ps):
 lo,hi=bounds(ps);return [(a+b)/2 for a,b in zip(lo,hi)]
for name,height,sha in [('BB8',0.67,'c813327f3abdb1e4823e7ff217a7e93d77b8d02dc1860f92fe8af151723955f4'),('WallE',1.016,'64f568ccf91098d18843a033c7d6ba770a6803b25f85a533e1957d03d5424b0e')]:
 folder=ROOT/name;assert hashlib.sha256((folder/'source.glb').read_bytes()).hexdigest()==sha
 g=Model(folder/'source.glb');m=g.m
 generated=folder/'Generated';generated.mkdir(exist_ok=True)
 for i,im in enumerate(m['images']):
  v=m['bufferViews'][im['bufferView']];ext='png' if im['mimeType']=='image/png' else 'jpg'
  (generated/f'texture-{i}.{ext}').write_bytes(g.b[v.get('byteOffset',0):v.get('byteOffset',0)+v['byteLength']])
 def texture(info):
  if not info:return None
  i=m['textures'][info['index']]['source'];ext='png' if m['images'][i]['mimeType']=='image/png' else 'jpg'
  return f'texture-{i}.{ext}'
 materials=[]
 for material in m['materials']:
  p=material.get('pbrMetallicRoughness',{});spec=material.get('extensions',{}).get('KHR_materials_pbrSpecularGlossiness',{})
  materials.append(dict(color=p.get('baseColorFactor',spec.get('diffuseFactor',[1,1,1,1])),base=texture(p.get('baseColorTexture',spec.get('diffuseTexture'))),normal=texture(material.get('normalTexture')),emission=texture(material.get('emissiveTexture')),metalRough=texture(p.get('metallicRoughnessTexture')),metal=p.get('metallicFactor',0.2),rough=p.get('roughnessFactor',0.5)))
 channels={}
 if name=='WallE':
  anim=m['animations'][0]
  for c in anim['channels']:
   sm=anim['samplers'][c['sampler']];channels[(c['target']['node'],c['target']['path'])]=(g.acc(sm['input']),g.acc(sm['output']))
 # Use the first authored pose; source rest transforms contain overlapping links.
 first={}
 for (node,path),(times,values) in channels.items():first.setdefault(node,{})[path]=values[0]
 raw=[];removed_body_cap=0
 for i,n in enumerate(m['nodes']):
  if 'mesh' not in n:continue
  a=g.matrix(i,first)
  for prim in m['meshes'][n['mesh']]['primitives']:
   at=prim['attributes'];ps=[vec(a,v) for v in g.acc(at['POSITION'])];ns=[]
   for v in g.acc(at['NORMAL']):
    nn=vec(a,v,0);ln=math.sqrt(sum(x*x for x in nn));ns.append([x/ln for x in nn])
   uv=g.acc(at['TEXCOORD_0']);idx=[v[0] for v in g.acc(prim['indices'])]
   groups={}
   for t in range(0,len(idx),3):
    ids=idx[t:t+3];center=[sum(ps[k][j] for k in ids)/3 for j in range(3)]
    if name=='BB8':
     # The pinned source includes a detached 32-triangle support disc just
     # above the sphere. It is hidden under the head at rest, but protrudes
     # when included in the rolling shell. Omit only that authored plane.
     if all(abs(ps[k][1]-17.80622)<0.0001 for k in ids):
      removed_body_cap+=1;continue
     role='ball' if max(ps[k][1] for k in ids)<18.3 else 'head';side=0
    else:
     label=n['name'];role='link' if label.startswith('track_low') else 'gear' if label.startswith('trackGears') else 'head' if label.startswith('eyes') or label.startswith('neck') else 'body'
     if label.startswith('hand'):role='arm'
     side=(1 if center[0]>0 else -1) if role in ['link','gear','arm'] else 0
    groups.setdefault((role,side),[]).extend(ids)
   for (role,side),ids in groups.items():
    unique=sorted(set(ids));remap={old:new for new,old in enumerate(unique)}
    raw.append(dict(name=n['name'],role=role,side=side,source=i,positions=[ps[k] for k in unique],normals=[ns[k] for k in unique],texcoords=[uv[k] for k in unique],indices=[remap[k] for k in ids],material=prim['material']))
 if name=='BB8':assert removed_body_cap==32, f'Unexpected BB-8 support disc: {removed_body_cap} triangles'
 allps=[v for p in raw for v in p['positions']];lo,hi=bounds(allps);scale=height*(.885/1.08)/(hi[1]-lo[1])
 running=[v for p in raw if p['role'] in ['ball','link'] for v in p['positions']];center=middle(running);origin=[center[0],lo[1],center[2]]
 def normalize(p):return [(p[k]-origin[k])*scale for k in range(3)]
 ballCenter=normalize(middle([v for p in raw if p['role']=='ball' for v in p['positions']])) if name=='BB8' else [0,0,0]
 headCenter=normalize(middle([v for p in raw if p['role']=='head' for v in p['positions']]))
 if name=='WallE':
  neck=[v for p in raw if p['name'].startswith('neck') for v in p['positions']]
  # Turn the binocular head/neck around its attachment to the chassis.
  bottom=min(v[1] for v in neck)
  headCenter=normalize(middle([v for v in neck if v[1]<bottom+0.03]))
 armPivots=[]
 if name=='WallE':
  # Original shoulder origins; the separate raised gripper follows its arm.
  for label in ['hand_low.001','hand_low']:
   node=next(i for i,n in enumerate(m['nodes']) if n.get('name')==label)
   armPivots.append(normalize(vec(g.matrix(node,first),[0,0,0])))
 parts=[];beltLength=0;contacts=[]
 for p in raw:
  ps=p['positions'];pivot=middle(ps)
  if name=='BB8':pivot=[ballCenter[k]/scale+origin[k] for k in range(3)] if p['role']=='ball' else [headCenter[k]/scale+origin[k] for k in range(3)]
  elif p['role']=='head':pivot=[headCenter[k]/scale+origin[k] for k in range(3)]
  elif p['role']=='arm':pivot=[armPivots[0 if p['side']>0 else 1][k]/scale+origin[k] for k in range(3)]
  frames=[]
  if p['role']=='link':
   parent=g.parents[p['source']];a0=g.matrix(p['source'],first);inv=inverse_rigid(a0)
   times,values=channels[(parent,'translation')];rots=channels[(parent,'rotation')][1]
   # First 253 keys are one exact 5.25-second loop at 48 fps.
   for j in range(253):
    override={k:dict(v) for k,v in first.items()};override[parent]={'translation':values[j],'rotation':rots[j]}
    delta=mul(g.matrix(p['source'],override),inv);pos=normalize(vec(delta,pivot))
    delta[12:15]=pos;frames.append(delta)
   if not beltLength:
    beltLength=sum(math.dist(a[12:15],b[12:15]) for a,b in zip(frames,frames[1:]))
  pp={k:v for k,v in p.items() if k not in ['source','positions']}
  pp.update(positions=[[(v[k]-pivot[k])*scale for k in range(3)] for v in ps],pivot=normalize(pivot),frames=frames)
  parts.append(pp)
 if name=='WallE':
  for side in [1,-1]:
   ps=[normalize(v) for p in raw if p['role']=='link' and p['side']==side for v in p['positions']];l,h=bounds(ps)
   contacts.append([(l[0]+h[0])/2,0,h[0]-l[0]])
 else:contacts=[[0,0,0.045]] # Visual contact patch; deformation is not simulated.
 result=dict(armPivots=armPivots,parts=parts,materials=materials,height=height*.885/1.08,ballCenter=ballCenter,ballRadius=ballCenter[1],headCenter=headCenter,beltLength=beltLength,contacts=contacts)
 (generated/'mesh.json').write_text(json.dumps(result,separators=(',',':')))
 print(name,sum(len(p['indices'])//3 for p in parts),'triangles',len(parts),'parts','height',result['height'],'belt',beltLength,'contacts',contacts)
