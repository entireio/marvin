#!/usr/bin/env python3
"""Add fleet recovery public trust to an existing factory CSV without changing identity."""
import argparse,csv,io,os
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--factory-csv',type=Path,required=True);p.add_argument('--public-key',type=Path,required=True);p.add_argument('--output-csv',type=Path,required=True);a=p.parse_args()
try:
 pem=a.public_key.read_bytes();key=serialization.load_pem_public_key(pem)
 if not isinstance(key,ec.EllipticCurvePublicKey) or not isinstance(key.curve,ec.SECP256R1) or len(pem)>=1024 or b'PRIVATE KEY' in pem:raise ValueError()
 text=a.factory_csv.read_text();
 if len(text)>65536:raise ValueError()
 rows=list(csv.reader(io.StringIO(text)))
 if len(rows)<2 or rows[0]!=['key','type','encoding','value'] or rows[1]!=['identity','namespace','','']:raise ValueError()
 names={r[0] for r in rows[2:] if len(r)==4}
 if len(names)!=len(rows)-2 or not {'salt','verifier','dev_key','device_id'}<=names:raise ValueError()
 existing=next((r for r in rows[2:] if r[0]=='recovery_pub'),None)
 expected=['recovery_pub','data','string',pem.decode('ascii')]
 if existing and existing!=expected:raise ValueError()
 if not existing:rows.append(expected)
 with open(a.output_csv,'x',opener=lambda path,flags:os.open(path,flags,0o600)) as out:csv.writer(out,lineterminator='\n').writerows(rows)
except (ValueError,OSError):p.exit(1,'Fleet recovery trust was not written: check the public key, factory format, and unused output path.\n')
print('Created a new factory CSV with fleet recovery public trust; identity and setup secret are unchanged.')
