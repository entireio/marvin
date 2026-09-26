#!/usr/bin/env python3
"""Derive a native mesh bundle from this repository's AP242 tessellated assembly.

Standard-library only. Intentionally supports the specific exporter dialect in
marvin_robot.step, not arbitrary STEP. Keeps all 23 part groups and their actual
assembly coordinates. CAD millimeters -> simulation units (100 mm), Y-up/+Z front.
The original CAD remains authoritative; this file never edits it.
"""
import hashlib
import json
import re
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'hardware/mechanical/step/marvin_robot.step'
OUTPUT = ROOT / 'apps/simulator-macos/Resources/Marvin'


def fields(value):
    result, depth, start = [], 0, 0
    for i, char in enumerate(value):
        if char == '(':
            depth += 1
        elif char == ')':
            depth -= 1
        elif char == ',' and depth == 0:
            result.append(value[start:i].strip())
            start = i + 1
    return result + [value[start:].strip()]


def triples(value, cast=float):
    return [[cast(x) for x in group.split(',')]
            for group in re.findall(r'\(([^()]+)\)', value)]


def main():
    raw = SOURCE.read_bytes()
    source = raw.decode()
    if not source.startswith('ISO-10303-21;'):
        raise ValueError('STEP geometry missing: run git lfs pull')
    entities = {int(i): (kind, body) for i, kind, body in re.findall(
        r'#(\d+)\s*=\s*(\w+)\((.*?)\);', source, re.S)}
    coordinates = {i: triples(fields(body)[2]) for i, (kind, body) in entities.items()
                   if kind == 'COORDINATES_LIST'}
    # Product and its triangulation appear together, in export order.
    parts, name = [], None
    for _, (kind, body) in entities.items():
        if kind == 'PRODUCT':
            name = fields(body)[0].strip("'")
        elif kind == 'TRIANGULATED_SURFACE_SET':
            data = fields(body)
            points = coordinates[int(data[1][1:])]
            mapping = [int(x) - 1 for x in data[4].strip('()').split(',')]
            faces = triples(data[5], int)
            if len(mapping) != len(points) or mapping != list(range(len(points))):
                raise ValueError('Unexpected STEP point mapping')
            if any(i < 1 or i > len(points) for f in faces for i in f):
                raise ValueError('Invalid triangle index')
            parts.append((name, points, faces))
    assert len(parts) == 23, f'Assembly changed: expected 23 parts, got {len(parts)}'
    floor = min(p[2] for _, ps, _ in parts for p in ps)
    blob = bytearray()
    manifest = {'source': str(SOURCE.relative_to(ROOT)),
                'sha256': hashlib.sha256(raw).hexdigest(),
                'license': 'CERN-OHL-S-2.0', 'millimetersPerUnit': 100, 'parts': []}
    triangle_count = 0
    for name, points, faces in parts:
        positions = [[p[1] / 100, (p[2]-floor) / 100, p[0] / 100] for p in points]
        # Area-weighted vertex normals preserve shared-vertex smoothing.
        normals = [[0.0, 0.0, 0.0] for _ in positions]
        for face in faces:
            a, b, c = [positions[i-1] for i in face]
            u, v = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
            n = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
            for index in face:
                for j in range(3):
                    normals[index-1][j] += n[j]
        vertex_offset = len(blob)
        for p, n in zip(positions, normals):
            length = sum(x*x for x in n) ** 0.5 or 1
            blob.extend(struct.pack('<6f', *p, *(x/length for x in n)))
        index_offset = len(blob)
        for face in faces:
            blob.extend(struct.pack('<3I', *(i-1 for i in face)))
        manifest['parts'].append({'name': name, 'vertexOffset': vertex_offset,
            'vertexCount': len(positions), 'indexOffset': index_offset,
            'triangleCount': len(faces)})
        triangle_count += len(faces)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / 'geometry.bin').write_bytes(blob)
    (OUTPUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(f'{OUTPUT.relative_to(ROOT)}: {len(parts)} parts, {triangle_count:,} triangles, {len(blob):,} bytes')


if __name__ == '__main__':
    main()
