"""Generate public test credentials/transcripts with the pinned ESP-IDF implementation."""
import sys,hashlib,json,os
from pathlib import Path
root=Path(__file__).resolve().parents[2]
os.environ['IDF_PATH']=str(root/'work/esp-idf')
sys.path.insert(0,str(root/'work/esp-idf/tools/esp_prov'))
from security.srp6a import Srp6a,calculate_x
from utils import long_to_bytes
import proto
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
username='test-marvin';password='fixture-only-setup-secret';secret=bytes(range(1,33));salt=bytes(range(16));c=Srp6a(username,password);c.a=int.from_bytes(secret,'big');c.A=pow(c.g,c.a,c.N)
b=123456789012345678901234567890123456789;v=pow(c.g,calculate_x(hashlib.sha512,int.from_bytes(salt,'big'),username,password),c.N);B=(c.k*v+pow(c.g,b,c.N))%c.N
proof=c.process_challenge(salt,long_to_bytes(B));c.verify_session(c.H_AMK);key=c.get_session_key()[:32]
p0=proto.session_pb2.SessionData();p0.sec_ver=2;p0.sec2.msg=1;p0.sec2.sr0.device_pubkey=long_to_bytes(B);p0.sec2.sr0.device_salt=salt
nonce=bytes(range(12));p1=proto.session_pb2.SessionData();p1.sec_ver=2;p1.sec2.msg=3;p1.sec2.sr1.device_proof=c.H_AMK;p1.sec2.sr1.device_nonce=nonce
plain=b'{"op":"scan"}';reply=b'{"phase":"scanning"}';nonce2=nonce[:8]+(int.from_bytes(nonce[8:],'big')+1).to_bytes(4,'big')
data=dict(username=username,password=password,secret=secret.hex(),salt=salt.hex(),serverPublic=long_to_bytes(B).hex(),publicKey=long_to_bytes(c.A).hex(),proof=proof.hex(),serverProof=c.H_AMK.hex(),key=key.hex(),response0=p0.SerializeToString().hex(),response1=p1.SerializeToString().hex(),ciphertext=AESGCM(key).encrypt(nonce,plain,None).hex(),encryptedReply=AESGCM(key).encrypt(nonce2,reply,None).hex())
(root/'tests/fixtures/security2.json').write_text(json.dumps(data,indent=2)+'\n')
