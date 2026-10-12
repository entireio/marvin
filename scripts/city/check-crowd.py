"""Verify trouser hems sit inside closed boots after posing and every LOD export."""
from pathlib import Path
import json, sys

path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[2] / 'apps/simulator-macos/Resources/City/crowd.json'
models = json.loads(path.read_text())
failures = []
checked = 0
for name, lods in models.items():
    for lod, model in enumerate(lods):
        vertices, indices = model['vertices'], model['indices']
        boots = [[vertices[indices[i + k]] for k in range(3)]
                 for i in range(0, len(indices), 3) if vertices[indices[i]][8] == 3]
        for side in (-1, 1):
            pants = [v for v in vertices if v[8] == 2 and v[0] * side > 0]
            bottom = min(v[1] for v in pants)
            hem = [v for v in pants if v[1] < bottom + .008]
            point = [sum(v[a] for v in hem) / len(hem) for a in range(3)]
            # A ray through the ankle center must start inside the boot volume.
            # Project each boot triangle into yz; barycentrics recover its x.
            hits = set()
            for a, b, c in boots:
                by, bz, cy, cz = b[1]-a[1], b[2]-a[2], c[1]-a[1], c[2]-a[2]
                det = by*cz-bz*cy
                if abs(det) < 1e-12:
                    continue
                py, pz = point[1]-a[1], point[2]-a[2]
                u, v = (py*cz-pz*cy)/det, (by*pz-bz*py)/det
                if u >= 0 and v >= 0 and u+v <= 1:
                    x = a[0]+u*(b[0]-a[0])+v*(c[0]-a[0])
                    if x > point[0]:
                        hits.add(round(x, 6))
            checked += 1
            if len(hits) % 2 != 1:
                failures.append(f'{name} LOD {lod}, ankle {side}')
if failures:
    sys.exit('Disconnected trouser/boot geometry: ' + ', '.join(failures))
print(f'Crowd boot fit: {checked} posed ankles enclosed across {len(models)} variants and all LODs')
