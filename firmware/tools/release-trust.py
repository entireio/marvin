"""Create a new factory CSV with release public trust, preserving device identity.
Never overwrites an existing file, replaces a release key, or flashes hardware.
"""
import argparse
import csv
import io
import os
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from release_key import public_release_key, bootstrap_capsule

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--factory-csv', type=Path, required=True)
p.add_argument('--public-key', type=Path, required=True)
p.add_argument('--output-csv', type=Path, required=True)
p.add_argument('--bootstrap-manifest', type=Path)
a = p.parse_args()
try:
    pem = public_release_key(a.public_key)
    capsule = bootstrap_capsule(a.bootstrap_manifest, pem) if a.bootstrap_manifest else None
    with a.factory_csv.open() as source:
        text = source.read(65537)
    if len(text) > 65536:
        raise ValueError('Factory CSV exceeds the reviewed size')
    rows = list(csv.reader(io.StringIO(text)))
    if len(rows) < 2 or rows[0] != ['key', 'type', 'encoding', 'value'] or rows[1] != ['identity', 'namespace', '', '']:
        raise ValueError('Unexpected factory CSV layout')
    names = set()
    for row in rows[2:]:
        if len(row) != 4 or row[1] != 'data' or row[0] in names:
            raise ValueError('Unexpected namespace or duplicate factory field')
        names.add(row[0])
        if row[0] == 'release_pub' and row != ['release_pub', 'data', 'hex2bin', (pem+b'\0').hex()]:
            raise ValueError('Existing release key differs; key rotation needs its own recovery procedure')
        if row[0] == 'boot_manifest' and capsule and row != ['boot_manifest', 'data', 'hex2bin', capsule.hex()]:
            raise ValueError('Existing bootstrap capsule differs')
        if row[0] == 'enroll_pub':
            enrollment = serialization.load_pem_public_key(row[3].encode()).public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)
            if enrollment == pem:
                raise ValueError('Firmware signing and enrollment signing require separate keys')
    if not {'salt', 'verifier', 'dev_key', 'device_id'} <= names:
        raise ValueError('Factory identity is incomplete')
    if 'release_pub' not in names:
        rows.append(['release_pub', 'data', 'hex2bin', (pem+b'\0').hex()])
    if capsule and 'boot_manifest' not in names:
        rows.append(['boot_manifest', 'data', 'hex2bin', capsule.hex()])
    with open(a.output_csv, 'x', opener=lambda path, flags: os.open(path, flags, 0o600)) as output:
        csv.writer(output, lineterminator='\n').writerows(rows)
except (ValueError, OSError):
    p.exit(1, 'Release trust was not written: check the public key, factory format, distinct signing keys and unused output path.\n')
print('Created a new factory CSV with public release trust; source identity preserved. Nothing flashed.')
