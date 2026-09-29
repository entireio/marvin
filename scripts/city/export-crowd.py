"""Offline Blender exporter. CC0 Blender Studio Human Base Meshes 1.4.1.
Usage: blender -b human_base_meshes_bundle.blend --python export-crowd.py -- OUTPUT
Ships posed, clothed indexed meshes; Blender is not required for normal builds.
"""
import bpy,sys,math,json
from pathlib import Path
from mathutils import Vector,Matrix
out=Path(sys.argv[sys.argv.index('--')+1]);out.mkdir(parents=True,exist_ok=True)
# Library subsets have no scene; link their objects before evaluating transforms.
for obj in list(bpy.data.objects):
 if not obj.users_collection:bpy.context.scene.collection.objects.link(obj)
bpy.context.view_layer.update()
sources={sex:bpy.data.objects['GEO-body_'+sex+'_realistic'] for sex in ['male','female']}
# The checked-in source contains only the required bodies and eyes.
selected=set(sources.values())
for sex in sources:
 selected.update(o for o in bpy.data.objects if o.name.startswith('GEO-body_'+sex+'_realistic.eye.'))
materials=[]
for n in ['skin','tunic','pants','boots','scarf','eyes','hair','hardware']:
 m=bpy.data.materials.new('crowd_'+n);materials.append(m)
def mesh(name,vertices,faces,slots):
 m=bpy.data.meshes.new(name);m.from_pydata(vertices,[],faces);m.update()
 o=bpy.data.objects.new(name,m);bpy.context.collection.objects.link(o)
 for mat in materials:m.materials.append(mat)
 for p,slot in zip(m.polygons,slots):p.material_index=slot;p.use_smooth=True
 return o

def build(sex,pose,variant):
 src=sources[sex];vs=[];faces=[];slots=[];arm_vertices=set()
 scale=1.69/max(v.co.z for v in src.data.vertices)
 raw=[Vector((v.co.x*scale,v.co.y*scale,v.co.z*scale)) for v in src.data.vertices]
 for i,p in enumerate(src.data.polygons):
  ids=list(p.vertices);c=sum((raw[i] for i in ids),Vector())/len(ids)
  arm=abs(c.x)>.22 and c.z<1.36
  hand=abs(c.x)>.375 and c.z<.84
  skin=c.z>1.44 or hand
  if not skin and not arm and c.z>.73:continue # shirt replaces torso/hip surface
  if c.z<.17:continue # separate closed boots replace exposed feet
  elif skin:slot=0
  elif arm:slot=1
  else:slot=2
  start=len(vs)
  for idx in ids:
   v=raw[idx].copy();normal=src.data.vertices[idx].normal
   if slot in [1,2,3]:
    fold=.0025*math.sin(v.z*43+v.x*31)+.0012*math.sin(v.z*87+v.y*33)
    v += normal*(.014+fold)
   if arm:arm_vertices.add(len(vs))
   vs.append(tuple(v))
  faces.append(tuple(range(start,start+len(ids))));slots.append(slot)
 # Tailored tunic with overlapping cloth folds and a shaped shoulder/neck line.
 rings=[(.70,.215,.145),(.76,.20,.13),(.86,.185,.12),(.99,.175,.12),(1.12,.195,.13),(1.25,.213,.145),(1.34,.235,.135),(1.40,.135,.088),(1.445,.065,.059)]
 if variant==2:rings=[(.36,.23,.15),(.49,.223,.146),(.60,.216,.14)]+rings
 n=48;start=len(vs)
 for j,(z,rx,ry) in enumerate(rings):
  for k in range(n):
   a=k*math.tau/n
   folds=(math.sin(a*11+z*9)*.008+math.sin(a*19-z*21)*.003)*(1 if z<1.30 else .3)
   vs.append(((rx+folds)*math.cos(a),(ry+folds)*math.sin(a)-.005,z))
 for j in range(len(rings)-1):
  for k in range(n):faces.append((start+j*n+k,start+j*n+(k+1)%n,start+(j+1)*n+(k+1)%n,start+(j+1)*n+k));slots.append(1)
 # Belt with front clasp; scarf is a draped, asymmetrical shoulder wrap.
 def loft(rings,slot,n=40):
  base=len(vs)
  for z,rx,ry in rings:
   for k in range(n):
    a=k*math.tau/n;vs.append((rx*math.cos(a),ry*math.sin(a),z))
  for j in range(len(rings)-1):
   for k in range(n):faces.append((base+j*n+k,base+j*n+(k+1)%n,base+(j+1)*n+(k+1)%n,base+(j+1)*n+k));slots.append(slot)
 loft([(1.005,.19,.135),(1.054,.19,.135)],3)
 loft([(1.365,.15,.109),(1.39,.134,.1),(1.417,.11,.083),(1.445,.075,.065),(1.46,.075,.070),(1.485,.071,.068)],4)
 # Loose scarf end on front of tunic, subtly rippled across the chest.
 base=len(vs)
 for j in range(13):
  z=1.40-j*.024
  for k in range(5):
   x=-.13+k*.025+(1.4-z)*.10;y=-.147-.009*math.sin(j*.9+k*.7)
   vs.append((x,y,z))
 for j in range(12):
  for k in range(4):faces.append((base+j*5+k,base+(j+1)*5+k,base+(j+1)*5+k+1,base+j*5+k+1));slots.append(4)
 # Skin face, eyelids, hands and fingers are the authored human mesh.
 # Eye geometry fills the authored sockets; iris and pupil colors are baked.
 for eye in [o for o in selected if o.name.startswith('GEO-body_'+sex+'_realistic.eye.')]:
  mat=src.matrix_world.inverted()@eye.matrix_world
  base=len(vs)
  eyevs=[mat@v.co for v in eye.data.vertices]
  vs.extend(tuple(v*scale) for v in eyevs)
  center=sum(eyevs,Vector())/len(eyevs)
  for poly in eye.data.polygons:
   c=sum((eyevs[k] for k in poly.vertices),Vector())/len(poly.vertices)
   face=(c.y-center.y)<-.008
   faces.append(tuple(base+k for k in poly.vertices));slots.append(6 if face else 5)
 # Hair cap on crown, preserving the front face. Different hairlines per variant.
 for poly in src.data.polygons:
  coords=[raw[k] for k in poly.vertices];c=sum(coords,Vector())/len(coords)
  if c.z>1.64-(.045 if c.y>-.045 else 0):
   base=len(vs)
   for idx in poly.vertices:vs.append(tuple(raw[idx]+src.data.vertices[idx].normal*.005))
   faces.append(tuple(range(base,len(vs))));slots.append(6)
 # Open-faced desert hood gives the long-robed variant a different silhouette.
 if variant==2:
  base=len(vs);n=24
  rings=[(1.41,.15,.14),(1.47,.124,.16),(1.56,.121,.164),(1.64,.105,.15),(1.70,.057,.083),(1.725,.004,.006)]
  for z,rx,ry in rings:
   for k in range(n+1):
    a=k*math.pi/n
    vs.append((rx*math.cos(a),-.076+ry*math.sin(a),z))
  for j in range(len(rings)-1):
   for k in range(n):
    faces.append((base+j*(n+1)+k,base+j*(n+1)+k+1,base+(j+1)*(n+1)+k+1,base+(j+1)*(n+1)+k));slots.append(4)
 # Closed boots with shaped toes, ankle shafts, and a thick sole.
 for side in [-1,1]:
  base=len(vs);n=24
  rings=[(.018,.060,.125,-.036),(.045,.060,.125,-.036),(.085,.056,.12,-.034),(.12,.050,.075,-.008),(.19,.046,.051,.004)]
  for z,rx,ry,cy in rings:
   for k in range(n):
    a=k*math.tau/n;vs.append((side*.095+rx*math.cos(a),cy+ry*math.sin(a),z))
  for j in range(len(rings)-1):
   for k in range(n):
    faces.append((base+j*n+k,base+j*n+(k+1)%n,base+(j+1)*n+(k+1)%n,base+(j+1)*n+k));slots.append(3)
 # Pose after clothing assembly, then recalculate smooth normals.
 posed=[]
 for vertex_index,rawv in enumerate(vs):
  v=Vector(rawv);x,y,z=v
  side=1 if x>0 else -1
  if vertex_index in arm_vertices and abs(x)>.205 and z<1.39 and z>.58:
   weight=max(0,min(1,(abs(x)-.19)/.06))
   pivot=Vector((side*.19,0,1.365))
   angle=side*(.26 if pose=='stand' else .34)
   r=Matrix.Rotation(angle,3,'Y');v=v.lerp(pivot+r@(v-pivot),weight)
   if z<1.09:
    elbow=Vector((side*.265,-.006,1.085))
    angle=(-.18 if variant==0 else -.43) if pose=='stand' else -.90
    v=v.lerp(elbow+Matrix.Rotation(angle,3,'X')@(v-elbow),min(1,(1.12-z)/.16))
  if z>1.44:
   pivot=Vector((0,-.02,1.44));v=pivot+Matrix.Rotation((variant-1)*.12,3,'Z')@(v-pivot)
  if pose=='sit':
   if z<.98 and abs(x)<.28:
    # Rotate the thigh around the hip while preserving its cross-section.
    # Then keep the shin vertical below the bent knee. Blend at both joints.
    upper=Vector((v.x,v.z-.89,.89-v.y))
    lower=Vector((v.x,v.y-.42,v.z+.42))
    knee=max(0,min(1,(z-.43)/.10));knee=knee*knee*(3-2*knee)
    bent=lower.lerp(upper,knee)
    hip=max(0,min(1,(.98-z)/.14));hip=hip*hip*(3-2*hip)
    v=v.lerp(bent,hip)
   v.z-=.60
  posed.append(tuple(v))
 o=mesh('Citizen_'+sex+'_'+pose+str(variant),posed,faces,slots)
 # Weld shared vertices, preserve material boundaries and decimate offline.
 bpy.context.view_layer.objects.active=o;o.select_set(True)
 mod=o.modifiers.new('weld','WELD');mod.merge_threshold=.0002;bpy.ops.object.modifier_apply(modifier=mod.name)
 o.data.update()
 return o

def export(o,target):
 dup=o.copy();dup.data=o.data.copy();bpy.context.collection.objects.link(dup)
 bpy.context.view_layer.objects.active=dup
 tri=dup.modifiers.new('tri','TRIANGULATE');bpy.ops.object.modifier_apply(modifier=tri.name)
 dec=dup.modifiers.new('lod','DECIMATE');dec.ratio=min(1,target/max(1,len(dup.data.polygons)));dec.use_collapse_triangulate=True
 bpy.ops.object.modifier_apply(modifier=dec.name)
 m=dup.data;m.calc_loop_triangles()
 for p in m.polygons:p.use_smooth=True
 m.update()
 # Indexed vertex data includes vertex normal and per-face semantic color slot.
 vertices=[];indices=[];lookup={}
 for t in m.loop_triangles:
  for idx in t.vertices:
   v=m.vertices[idx];p=v.co;n=v.normal;key=(idx,t.material_index)
   if key not in lookup:
    lookup[key]=len(vertices)
    # Z-up, -Y front => Y-up, +Z front. Realistic human scales to 1.03 game units.
    vertices.append([round(p.x*.61,6),round(p.z*.61,6),round(-p.y*.61,6),round(n.x,6),round(n.z,6),round(-n.y,6),round(p.x*3,5),round(p.z*3,5),t.material_index])
   indices.append(lookup[key])
 bpy.data.objects.remove(dup,do_unlink=True)
 return {'vertices':vertices,'indices':indices}
result={}
for sex in ['male','female']:
 for pose in ['stand','sit']:
  for variant in range(3):
   o=build(sex,pose,variant)
   result[f'{sex}-{pose}-{variant}']=[export(o,t) for t in [6500,1600,350]]
   print('EXPORTED',sex,pose,variant,[len(v['indices'])//3 for v in result[f'{sex}-{pose}-{variant}']],flush=True)
   bpy.data.objects.remove(o,do_unlink=True)
(out/'crowd.json').write_text(json.dumps(result,separators=(',',':')))
print('crowd bytes',(out/'crowd.json').stat().st_size)
