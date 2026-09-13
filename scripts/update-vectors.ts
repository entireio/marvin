import {generateKeyPairSync,sign,createHash} from 'node:crypto';
import {mkdirSync,writeFileSync} from 'node:fs';
const directory='work/firmware-update-vectors';mkdirSync(directory,{recursive:true,mode:0o700});
const key=generateKeyPairSync('ec',{namedCurve:'prime256v1'});
writeFileSync(directory+'/public.pem',key.publicKey.export({type:'spki',format:'pem'}),{mode:0o600});
const payload=Buffer.alloc(112);payload.write('MRVOTA01');payload.writeUInt32BE(4,8);payload.writeUInt32BE(4096,12);payload.fill(0x5a,16,48);payload.write('waveshare-esp32s3-audio',48);payload.write('afe-v1',80);payload.writeUInt32BE(2,96);
let count=0;
function save(name:string,p:Buffer,expected:boolean,signature=sign('sha256',p,{key:key.privateKey,dsaEncoding:'ieee-p1363'})){
 writeFileSync(`${directory}/${expected?'yes':'no'}-${name}.bin`,Buffer.concat([p,signature]),{mode:0o600});count++;
}
save('valid',payload,true);
for(const [name,offset,value] of [['version',7,50],['board',48,88],['layout',80,88],['padding',79,1],['reserved',100,1]] as const){const p=Buffer.from(payload);p[offset]=value;save(name,p,false);}
for(const [name,offset,value] of [['stale',8,3],['downgrade',8,1],['zero-sequence',8,0],['too-small',12,1023],['too-large',12,0x1e0001],['missing-prior',96,4]] as const){const p=Buffer.from(payload);p.writeUInt32BE(value,offset);save(name,p,false);}
const bad=Buffer.from(payload);bad[20]^=1;save('modified-digest',bad,false,sign('sha256',payload,{key:key.privateKey,dsaEncoding:'ieee-p1363'}));
const wrongKey=generateKeyPairSync('ec',{namedCurve:'prime256v1'});save('wrong-key',payload,false,sign('sha256',payload,{key:wrongKey.privateKey,dsaEncoding:'ieee-p1363'}));
save('zero-signature',payload,false,Buffer.alloc(64));
console.log(JSON.stringify({vectors:count,privateKeyWritten:false}));
const image=Buffer.alloc(4096,0x42),transfer=Buffer.from(payload);createHash('sha256').update(image).digest().copy(transfer,16);
writeFileSync(directory+'/transfer.bin',Buffer.concat([transfer,sign('sha256',transfer,{key:key.privateKey,dsaEncoding:'ieee-p1363'})]),{mode:0o600});

const bootstrap=Buffer.from(transfer);bootstrap.writeUInt32BE(1,8);bootstrap.writeUInt32BE(0,96);writeFileSync(directory+'/bootstrap.bin',Buffer.concat([bootstrap,sign('sha256',bootstrap,{key:key.privateKey,dsaEncoding:'ieee-p1363'})]),{mode:0o600});
