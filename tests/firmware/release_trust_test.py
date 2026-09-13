"""Factory trust fixture; never accesses existing board credentials."""
import csv
import io
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='marvin-trust-') as directory:
    d = Path(directory)
    signer = ec.generate_private_key(ec.SECP256R1())
    key = signer.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)
    (d/'public.pem').write_bytes(key)
    rows = [['key','type','encoding','value'],['identity','namespace','',''],['salt','data','hex2bin','01'],['verifier','data','hex2bin','02'],['dev_key','data','hex2bin','03'],['device_id','data','string','fixture-device']]
    def source(values):
        with (d/'source.csv').open('w') as f: csv.writer(f).writerows(values)
    def run(output='out.csv'):
        return subprocess.run([sys.executable,str(root/'firmware/tools/release-trust.py'),'--factory-csv',str(d/'source.csv'),'--public-key',str(d/'public.pem'),'--output-csv',str(d/output)],capture_output=True).returncode
    source(rows);before=(d/'source.csv').read_bytes();assert run()==0
    result=list(csv.reader(io.StringIO((d/'out.csv').read_text())))
    assert result[:-1]==rows and bytes.fromhex(result[-1][3])==key+b'\0'
    assert (d/'source.csv').read_bytes()==before and (d/'out.csv').stat().st_mode&0o777==0o600
    assert run()!=0
    source(rows+[['enroll_pub','data','string',key.decode()]]);assert run('reuse.csv')!=0
    source(rows+[['release_pub','data','hex2bin','00']]);assert run('rotate.csv')!=0
    source(rows+[rows[-1]]);assert run('duplicate.csv')!=0
    source(rows);(d/'public.pem').write_bytes(signer.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()));assert run('private.csv')!=0
    (d/'public.pem').write_bytes(ec.generate_private_key(ec.SECP384R1()).public_key().public_bytes(serialization.Encoding.PEM,serialization.PublicFormat.SubjectPublicKeyInfo));assert run('curve.csv')!=0
    (d/'public.pem').write_bytes(key)
    created = subprocess.run([sys.executable,str(root/'firmware/tools/factory.py'),'--output',str(d/'new-factory'),'--release-public-key',str(d/'public.pem')],env={**os.environ,'IDF_PATH':str(root/'work/esp-idf')},capture_output=True)
    assert created.returncode == 0
    generated = list(csv.reader(io.StringIO((d/'new-factory/factory.csv').read_text())))
    release = next(row for row in generated if row[0]=='release_pub')
    assert bytes.fromhex(release[3]) == key+b'\0'
print('Factory release trust fixture passed: identity preserved, private permissions, no overwrite, no signing-key reuse/rotation, duplicate/wrong-curve rejection. No board touched.')
# Signature validation uses only an ephemeral fixture signer; no board files.
from hashlib import sha256
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature
sys.path.insert(0,str(root/'firmware/tools'))
from release_key import bootstrap_capsule
with tempfile.TemporaryDirectory(prefix='marvin-bootstrap-trust-') as directory:
    d=Path(directory);signer=ec.generate_private_key(ec.SECP256R1());public=signer.public_key().public_bytes(serialization.Encoding.PEM,serialization.PublicFormat.SubjectPublicKeyInfo)
    payload=bytearray(112);payload[:8]=b'MRVOTA01';payload[8:12]=(1).to_bytes(4,'big');payload[12:16]=(1024).to_bytes(4,'big');payload[16:48]=sha256(bytes(1024)).digest();payload[48:80]=b'waveshare-esp32s3-audio'.ljust(32,b'\0');payload[80:96]=b'afe-v1'.ljust(16,b'\0')
    r,s=decode_dss_signature(signer.sign(bytes(payload),ec.ECDSA(hashes.SHA256())));capsule=bytes(payload)+r.to_bytes(32,'big')+s.to_bytes(32,'big');path=d/'manifest.bin';path.write_bytes(capsule);assert bootstrap_capsule(path,public)==capsule
    for position in (8,16,48,80,96,100,175):
        bad=bytearray(capsule);bad[position]^=1;path.write_bytes(bad)
        try: bootstrap_capsule(path,public)
        except ValueError: pass
        else: raise AssertionError('Modified capsule accepted')
print('Bootstrap capsule signature/board/layout/prior-version/tamper fixture passed.')
