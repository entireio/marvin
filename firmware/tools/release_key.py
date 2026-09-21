"""Public-only firmware trust shared by factory tooling; never creates a signer."""
from pathlib import Path
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

def public_release_key(path: Path) -> bytes:
    with path.open('rb') as stream:
        pem = stream.read(1025)
    if len(pem) > 1024 or not pem.startswith(b'-----BEGIN PUBLIC KEY-----'):
        raise ValueError('Use only a bounded PEM public firmware key')
    key = serialization.load_pem_public_key(pem)
    if not isinstance(key, ec.EllipticCurvePublicKey) or not isinstance(key.curve, ec.SECP256R1):
        raise ValueError('Firmware release verification requires a public P-256 key')
    return key.public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo)

def bootstrap_capsule(path: Path, public_pem: bytes) -> bytes:
    from cryptography.exceptions import InvalidSignature
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric.utils import encode_dss_signature
    with path.open('rb') as stream:
        data = stream.read(177)
    if len(data) != 176 or data[:8] != b'MRVOTA01' or any(data[100:112]):
        raise ValueError('Invalid bootstrap capsule')
    board = b'waveshare-esp32s3-audio'.ljust(32, b'\0')
    layouts = (b'afe-v1'.ljust(16,b'\0'), b'afe-v2'.ljust(16,b'\0'), b'afe-v3'.ljust(16,b'\0'), b'owner-v1'.ljust(16,b'\0'))
    if data[48:80] != board or data[80:96] not in layouts or not int.from_bytes(data[8:12],'big') or not 1024 <= int.from_bytes(data[12:16],'big') <= 0x400000 or int.from_bytes(data[96:100],'big') != 0:
        raise ValueError('Bootstrap must target this board/layout and initial sequence')
    signature = encode_dss_signature(int.from_bytes(data[112:144],'big'), int.from_bytes(data[144:176],'big'))
    try:
        serialization.load_pem_public_key(public_pem).verify(signature, data[:112], ec.ECDSA(hashes.SHA256()))
    except InvalidSignature as error:
        raise ValueError('Bootstrap signature does not match release trust') from error
    return data
