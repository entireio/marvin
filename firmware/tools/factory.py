"""Generate unique bench credentials; never flash or print them automatically."""
import argparse, importlib.util, json, os, secrets, sys
from pathlib import Path
from hashlib import sha256
from release_key import public_release_key, bootstrap_capsule
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
p.add_argument('--release-public-key', type=Path)
p.add_argument('--bootstrap-manifest', type=Path)
p.add_argument('--recovery-public-key', type=Path)
a = p.parse_args()
if a.bootstrap_manifest and not a.release_public_key:
    p.error('A bootstrap capsule requires its dedicated release public key')
release_row = ''
recovery_row = ''
if a.release_public_key:
    try:
        release_pem = public_release_key(a.release_public_key)
        release_row = 'release_pub,data,hex2bin,' + (release_pem+b'\0').hex() + '\n'
        if a.bootstrap_manifest:
            release_row += 'boot_manifest,data,hex2bin,' + bootstrap_capsule(a.bootstrap_manifest, release_pem).hex() + '\n'
    except (ValueError, OSError):
        p.error('Use a bounded public P-256 release key; private keys are not accepted')
if a.recovery_public_key:
    try:
        recovery_pem=a.recovery_public_key.read_bytes();recovery_key=serialization.load_pem_public_key(recovery_pem)
        if not isinstance(recovery_key,ec.EllipticCurvePublicKey) or not isinstance(recovery_key.curve,ec.SECP256R1) or len(recovery_pem)>=1024 or b'PRIVATE KEY' in recovery_pem:raise ValueError()
        recovery_row='recovery_pub,data,string,"'+recovery_pem.decode('ascii').replace('"','""')+'"\n'
    except (ValueError,OSError):p.error('Use a bounded public P-256 fleet recovery key; private keys are not accepted')
idf = Path(os.environ['IDF_PATH'])
sys.path.insert(0, str(idf / 'tools/esp_prov'))
spec = importlib.util.spec_from_file_location('srp6a', idf / 'tools/esp_prov/security/srp6a.py')
srp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(srp)
a.output.mkdir(mode=0o700, parents=True, exist_ok=False)
device_key=ec.generate_private_key(ec.SECP256R1())
private_der=device_key.private_bytes(serialization.Encoding.DER,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())
public_der=device_key.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)
device_id='marvin_'+sha256(public_der).hexdigest()[:32]
username = 'marvin-' + secrets.token_hex(8)
password = secrets.token_urlsafe(24)
salt, verifier = srp.generate_salt_and_verifier(username, password, 16)
verifier = verifier.rjust(384, b'\0')
for name, content in {
    'setup-secret.json': json.dumps({'username': username, 'password': password, 'security': 2, 'patch': 1, 'deviceId':device_id}),
    'device-public.pem': device_key.public_key().public_bytes(serialization.Encoding.PEM,serialization.PublicFormat.SubjectPublicKeyInfo).decode(),
    'factory.csv': 'key,type,encoding,value\nidentity,namespace,,\nsalt,data,hex2bin,' + salt.hex() + '\nverifier,data,hex2bin,' + verifier.hex() + '\ndev_key,data,hex2bin,' + private_der.hex() + '\ndevice_id,data,string,'+device_id+'\n'+release_row+recovery_row,
}.items():
    with open(a.output / name, 'x', opener=lambda path, flags: os.open(path, flags, 0o600)) as f:
        f.write(content)
print('Created private factory CSV and setup credential file. See firmware/README.md for NVS image generation.')
