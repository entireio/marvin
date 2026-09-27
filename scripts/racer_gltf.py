"""Small offline glTF reader for the pinned BB-8 and WALL-E assets."""
import json,struct,math
from pathlib import Path

def mul(a,b): return [sum(a[k*4+r]*b[c*4+k] for k in range(4)) for c in range(4) for r in range(4)]
def mat(n):
 if 'matrix' in n:return n['matrix']
 x,y,z,w=n.get('rotation',[0,0,0,1]);s=n.get('scale',[1]*3);t=n.get('translation',[0]*3)
 return [(1-2*y*y-2*z*z)*s[0],(2*x*y+2*w*z)*s[0],(2*x*z-2*w*y)*s[0],0,(2*x*y-2*w*z)*s[1],(1-2*x*x-2*z*z)*s[1],(2*y*z+2*w*x)*s[1],0,(2*x*z+2*w*y)*s[2],(2*y*z-2*w*x)*s[2],(1-2*x*x-2*y*y)*s[2],0,*t,1]
def vec(m,v,w=1):return [sum(m[c*4+r]*v[c] for c in range(3))+m[12+r]*w for r in range(3)]
class Model:
 def __init__(self,path):
  raw=Path(path).read_bytes();n=struct.unpack_from('<I',raw,12)[0];self.m=json.loads(raw[20:20+n]);self.b=raw[28+n:];self.parents={c:i for i,n in enumerate(self.m['nodes']) for c in n.get('children',[])}
 def acc(self,i):
  a=self.m['accessors'][i];v=self.m['bufferViews'][a['bufferView']];count={'SCALAR':1,'VEC2':2,'VEC3':3,'VEC4':4,'MAT4':16}[a['type']];fmt='<'+{5126:'f',5123:'H',5125:'I',5121:'B'}[a['componentType']]*count;stride=v.get('byteStride',struct.calcsize(fmt));return [struct.unpack_from(fmt,self.b,v.get('byteOffset',0)+a.get('byteOffset',0)+j*stride) for j in range(a['count'])]
 def matrix(self,i,override=None):
  n=self.m['nodes'][i];a=mat(dict(n,**(override or {}).get(i,{})))
  return mul(self.matrix(self.parents[i],override),a) if i in self.parents else a
 def positions(self,i,override=None):
  n=self.m['nodes'][i];p=self.m['meshes'][n['mesh']]['primitives'][0]
  return [vec(self.matrix(i,override),v) for v in self.acc(p['attributes']['POSITION'])]
