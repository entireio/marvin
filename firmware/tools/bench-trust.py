"""Add a reviewed HTTPS probe and public CA to existing private factory credentials.
This is a bench configuration, not browser enrollment or production ownership.
"""
import argparse,csv,io,os
from pathlib import Path
from urllib.parse import urlsplit
from cryptography import x509
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--factory',required=True,type=Path);p.add_argument('--origin',required=True);p.add_argument('--ca',required=True,type=Path);p.add_argument('--enrollment-public-key',type=Path);a=p.parse_args()
u=urlsplit(a.origin)
if u.scheme!='https' or not u.hostname or u.hostname in ('localhost','127.0.0.1','::1') or u.username or u.password or u.path not in ('','/') or u.query or u.fragment:p.error('Use the robot-reachable HTTPS origin without credentials or a path')
probe=a.origin.rstrip('/')+'/api/health'
if len(probe.encode())>=256:p.error('Probe URL is too long')
ca=a.ca.read_bytes();x509.load_pem_x509_certificate(ca)
if len(ca)>=2048 or b'PRIVATE KEY' in ca:p.error('Use only a bounded public CA certificate')
path=a.factory/'factory.csv';rows=list(csv.reader(io.StringIO(path.read_text())))
if rows[1]!=['identity','namespace','','']:raise RuntimeError('Unexpected factory namespace')
rows=[r for r in rows if r[0] not in ('probe_url','probe_ca')]
if a.enrollment_public_key:
    pem=a.enrollment_public_key.read_bytes();key=serialization.load_pem_public_key(pem)
    if not isinstance(key,ec.EllipticCurvePublicKey) or not isinstance(key.curve,ec.SECP256R1) or len(pem)>=1024 or b'PRIVATE KEY' in pem:p.error('Use a bounded public P-256 enrollment key')
    rows=[r for r in rows if r[0] not in ('enroll_iss','enroll_pub')]
    rows += [['enroll_iss','data','string',a.origin.rstrip('/')],['enroll_pub','data','string',pem.decode('ascii')]]
rows += [['probe_url','data','string',probe],['probe_ca','data','hex2bin',ca.hex()]]
out=io.StringIO();csv.writer(out,lineterminator='\n').writerows(rows);temporary=path.with_suffix('.csv.tmp')
with open(temporary,'x',opener=lambda name,flags:os.open(name,flags,0o600)) as f:f.write(out.getvalue())
os.replace(temporary,path);print('Bench public trust added; private identity and setup secret preserved. Regenerate the NVS image before flashing.')
