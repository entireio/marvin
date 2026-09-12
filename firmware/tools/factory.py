"""Generate unique bench credentials; never flash or print them automatically."""
import argparse, importlib.util, json, os, secrets, sys
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
idf = Path(os.environ['IDF_PATH'])
sys.path.insert(0, str(idf / 'tools/esp_prov'))
spec = importlib.util.spec_from_file_location('srp6a', idf / 'tools/esp_prov/security/srp6a.py')
srp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(srp)
a.output.mkdir(mode=0o700, parents=True, exist_ok=False)
username = 'marvin-' + secrets.token_hex(8)
password = secrets.token_urlsafe(24)
salt, verifier = srp.generate_salt_and_verifier(username, password, 16)
verifier = verifier.rjust(384, b'\0')
for name, content in {
    'setup-secret.json': json.dumps({'username': username, 'password': password, 'security': 2, 'patch': 1}),
    'factory.csv': 'key,type,encoding,value\nidentity,namespace,,\nsalt,data,hex2bin,' + salt.hex() + '\nverifier,data,hex2bin,' + verifier.hex() + '\n',
}.items():
    with open(a.output / name, 'x', opener=lambda path, flags: os.open(path, flags, 0o600)) as f:
        f.write(content)
print('Created private factory CSV and setup credential file. See firmware/README.md for NVS image generation.')
