#!/usr/bin/env python3
"""Bake LordDiego's attributed R2-D2 into its deployed three-leg driving pose.

Reads the pinned public glTF conversion and original texture archive offline.
Preserves smooth normals/UVs, excludes the presentation floor, and retains a
separate head pivot. Standard library only; no Blender/runtime dependencies.
"""
import bisect
import hashlib
import io
import json
import math
from pathlib import Path
import struct
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DIRECTORY = ROOT / 'apps/simulator-macos/Resources/R2D2'
raw = (DIRECTORY / 'source-gltf.zip').read_bytes()
assert hashlib.sha256(raw).hexdigest() == 'ae567fae88d38c22b43b897cae16dd0999887c1016a17a074cae1cb29e559f74'
archive = zipfile.ZipFile(io.BytesIO(raw))
model = json.loads(archive.read('index.gltf'))
binary = archive.read('index.bin')
original = (DIRECTORY / 'source-original.zip').read_bytes()
assert hashlib.sha256(original).hexdigest() == 'b2535b16ff7e142a515ca9be2c380680ee645521f54628a9761439fb34449216'
textures = zipfile.ZipFile(io.BytesIO(original))
texture_dir = DIRECTORY / 'Textures'
texture_dir.mkdir(exist_ok=True)
for name in ['R2D2_Base_Color.png', 'R2D2_Metalness.png', 'R2D2_Roughness.png',
             'R2D2_Emission.png', 'R2D2_Barrel_Base.png', 'R2D2_Barrel_Normal.png', 'R2D2_Barrel_Roughness.png']:
    (texture_dir / name).write_bytes(textures.read('textures/'+name))

def accessor(index):
    a = model['accessors'][index]
    v = model['bufferViews'][a['bufferView']]
    assert v['buffer'] == 0 and 'sparse' not in a
    count = {'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'SCALAR': 1}[a['type']]
    fmt = '<' + {5126: 'f', 5123: 'H', 5125: 'I'}[a['componentType']]*count
    stride = v.get('byteStride', struct.calcsize(fmt))
    offset = v.get('byteOffset', 0)+a.get('byteOffset', 0)
    return [struct.unpack_from(fmt, binary, offset+i*stride) for i in range(a['count'])]

# The source's 4-second frame deploys the center leg and inclines the body.
# Keep the dome facing ahead and omit the presentation turntable animation.
for animation in model['animations']:
    for channel in animation['channels']:
        index, path = channel['target']['node'], channel['target']['path']
        if index in [8, 16]:
            continue
        sampler = animation['samplers'][channel['sampler']]
        times = [t[0] for t in accessor(sampler['input'])]
        values = accessor(sampler['output'])
        frame = max(0, min(len(times)-1, bisect.bisect_left(times, 4.0)))
        model['nodes'][index][path] = values[frame]

def rotate(v, q):
    x, y, z, w = q
    tx, ty, tz = 2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0])
    return [v[0]+w*tx+y*tz-z*ty, v[1]+w*ty+z*tx-x*tz, v[2]+w*tz+x*ty-y*tx]

parents = {child: i for i, n in enumerate(model['nodes']) for child in n.get('children', [])}
def transform(v, index, normal=False):
    while True:
        node = model['nodes'][index]
        scale = node.get('scale', [1, 1, 1])
        v = [v[i]/scale[i] if normal else v[i]*scale[i] for i in range(3)]
        v = rotate(v, node.get('rotation', [0, 0, 0, 1]))
        if not normal:
            v = [v[i]+node.get('translation', [0, 0, 0])[i] for i in range(3)]
        if index not in parents:
            break
        index = parents[index]
    if normal:
        length = math.sqrt(sum(c*c for c in v))
        v = [c/length for c in v]
    return v

parts = []
for i, node in enumerate(model['nodes']):
    if 'mesh' not in node or node['name'] == 'Circle.005':
        continue
    for primitive in model['meshes'][node['mesh']]['primitives']:
        assert primitive.get('mode', 4) == 4
        attributes = primitive['attributes']
        parts.append(dict(name=node['name'], head=i in [3, 4, 5, 6, 7, 8],
                          positions=[transform(v, i) for v in accessor(attributes['POSITION'])],
                          normals=[transform(v, i, True) for v in accessor(attributes['NORMAL'])],
                          texcoords=accessor(attributes['TEXCOORD_0']),
                          indices=[v[0] for v in accessor(primitive['indices'])],
                          material=primitive['material']))
vertices = [v for p in parts for v in p['positions']]
low = [min(v[i] for v in vertices) for i in range(3)]
high = [max(v[i] for v in vertices) for i in range(3)]
scale = 0.85/(high[1]-low[1])
origin = [(low[0]+high[0])/2, low[1], (low[2]+high[2])/2]
# Raise the foot shells slightly above the added, functional rolling tires.
def normalized(v):
    return [round((v[i]-origin[i])*scale+(0.035 if i == 1 else 0), 7) for i in range(3)]
for part in parts:
    part['positions'] = [normalized(v) for v in part['positions']]
    part['normals'] = [[round(c, 7) for c in v] for v in part['normals']]
    part['texcoords'] = [[round(c, 7) for c in v] for v in part['texcoords']]

result = dict(sourceSHA256=hashlib.sha256(raw).hexdigest(), headPivot=normalized(transform([0, 0, 0], 8)), headAxis=transform([0, 1, 0], 8, True), parts=parts)
(DIRECTORY / 'mesh.json').write_text(json.dumps(result, separators=(',', ':'))+'\n')
print(f"R2-D2: {sum(len(p['indices'])//3 for p in parts)} triangles, {len(parts)} material groups")
