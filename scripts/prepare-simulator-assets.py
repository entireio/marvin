#!/usr/bin/env python3
"""Run offline exporters only when their inputs or generated outputs change."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
RESOURCES = Path('apps/simulator-macos/Resources')
CACHE = ROOT / 'apps/simulator-macos/.build/asset-cache'
JOBS = [
    ('Marvin', 'export-marvin-simulator.py',
     ['hardware/mechanical/step/marvin_robot.step'],
     ['Marvin/geometry.bin', 'Marvin/manifest.json']),
    ('R2-D2', 'export-r2d2-simulator.py',
     [str(RESOURCES / 'R2D2/source-gltf.zip'),
      str(RESOURCES / 'R2D2/source-original.zip')],
     ['R2D2/mesh.json', 'R2D2/Textures']),
    ('BB-8 and WALL-E', 'export-racers-simulator.py',
     [str(RESOURCES / 'BB8/source.glb'), str(RESOURCES / 'WallE/source.glb'),
      'scripts/racer_gltf.py'],
     ['BB8/Generated', 'WallE/Generated']),
]


def fingerprints(paths):
    """Hash contents, including the file inventory of output directories."""
    result = {}
    for relative in paths:
        path = ROOT / relative
        files = sorted(p for p in path.rglob('*') if p.is_file()) if path.is_dir() else [path]
        if not files:
            raise FileNotFoundError(f'No generated files in {path}')
        for file in files:
            digest = hashlib.sha256()
            with file.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(chunk)
            result[str(file.relative_to(ROOT))] = digest.hexdigest()
    return result


def prepare(name, exporter, sources, outputs):
    inputs = fingerprints(['scripts/' + exporter, 'scripts/prepare-simulator-assets.py', *sources])
    output_paths = [RESOURCES / path for path in outputs]
    stamp = CACHE / (exporter + '.json')
    try:
        previous = json.loads(stamp.read_text())
        current = {'inputs': inputs, 'outputs': fingerprints(output_paths)}
    except (OSError, ValueError):
        previous, current = None, {}
    if previous == current:
        print(f'{name}: assets unchanged; skipping export', flush=True)
        return
    if os.environ.get('MARVIN_PROGRESS') == '1':
        print('\x1e' + f'Generating {name} assets', flush=True)
    print(f'{name}: generating assets', flush=True)
    # Invalidate before running: a failed export must never leave a valid stamp.
    stamp.unlink(missing_ok=True)
    subprocess.run([sys.executable, str(ROOT / 'scripts' / exporter)], check=True)
    CACHE.mkdir(parents=True, exist_ok=True)
    stamp.write_text(json.dumps({'inputs': inputs, 'outputs': fingerprints(output_paths)}))


if __name__ == '__main__':
    for job in JOBS:
        prepare(*job)
