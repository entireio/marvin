import {generateKeyPairSync,createHash,verify} from 'node:crypto';
import {mkdir,writeFile} from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import {challengeMessage,redemptionMessage} from '../packages/enrollment/src/service.js';
const dir='work/identity-vectors';await mkdir(dir,{recursive:true,mode:0o700});
const tests:unknown[]=[];
for(const [curve,format] of [['prime256v1','sec1'],['prime256v1','pkcs8'],['secp384r1','pkcs8']] as const){
 const {privateKey,publicKey}=generateKeyPairSync('ec',{namedCurve:curve}),id='marvin_'+createHash('sha256').update(publicKey.export({format:'der',type:'spki'})).digest('hex').slice(0,32),path=dir+'/'+curve+'-'+format+'.der';await writeFile(path,privateKey.export({format:'der',type:format}),{mode:0o600});
 const output=execFileSync('work/board/identity-test',[path,id],{encoding:'utf8'}).trim().split('\n');
 if(curve==='prime256v1'){
  const challenge=JSON.parse(output[0]);if(!verify('sha256',Buffer.from(challengeMessage(challenge.challenge)),{key:publicKey,dsaEncoding:'ieee-p1363'},Buffer.from(challenge.proof,'base64url')))throw new Error('Firmware challenge proof differs from backend');
  if(!verify('sha256',Buffer.from(redemptionMessage('synthetic.ticket.signature','Café test')),{key:publicKey,dsaEncoding:'ieee-p1363'},Buffer.from(output[1],'base64url')))throw new Error('Firmware redemption proof differs from backend');
  if(execFileSync('work/board/identity-test',[path,'marvin_'+'0'.repeat(32)],{encoding:'utf8'}).trim()!=='rejected')throw new Error('Mismatched fingerprint accepted');
  tests.push({case:format+' P256 challenge and UTF8-network redemption verified by actual backend format',passed:true},{case:'Wrong device fingerprint rejected',passed:true});
 }else{if(output[0]!=='rejected')throw new Error('Wrong curve accepted');tests.push({case:'P384 identity rejected',passed:true});}
}
await writeFile(dir+'/results.json',JSON.stringify({at:new Date().toISOString(),tests,scope:'Actual device_identity.c linked to synthetic host NVS and host RNG; real ESP-IDF MbedTLS. No physical-device or factory-secret access.'},null,2));console.log(JSON.stringify(tests));
