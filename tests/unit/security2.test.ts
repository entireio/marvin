import {it,expect,vi} from 'vitest';
import {deriveSrp,Security2} from '../../packages/provisioning/src/security2.js';
import fixture from '../fixtures/security2.json';
const bytes=(s:string)=>new Uint8Array(Buffer.from(s,'hex'));
it('SRP public key, shared key and mutual proofs match ESP-IDF 5.4.2 Security 2',async()=>{const result=await deriveSrp(fixture.username,fixture.password,bytes(fixture.secret),bytes(fixture.salt),bytes(fixture.serverPublic));for(const key of ['publicKey','key','proof','serverProof'] as const)expect(Buffer.from(result[key]).toString('hex')).toBe(fixture[key]);});
it('protobuf negotiation and both AES-GCM nonce directions interoperate with Espressif fixtures',async()=>{const entropy=vi.spyOn(crypto,'getRandomValues').mockImplementation((a:any)=>{a.set(bytes(fixture.secret));return a;});try{const secure=new Security2();let i=0;await secure.open(fixture.username,fixture.password,async()=>bytes(i++===0?fixture.response0:fixture.response1));const reply=await secure.exchange(new TextEncoder().encode('{"op":"scan"}'),async data=>{expect(Buffer.from(data).toString('hex')).toBe(fixture.ciphertext);return bytes(fixture.encryptedReply);});expect(new TextDecoder().decode(reply)).toBe('{"phase":"scanning"}');secure.close();await expect(secure.exchange(new Uint8Array(),async v=>v)).rejects.toThrow();}finally{entropy.mockRestore();}});
it('invalid server values and incorrect mutual proof fail closed',async()=>{await expect(deriveSrp(fixture.username,fixture.password,bytes(fixture.secret),bytes(fixture.salt),new Uint8Array([0]))).rejects.toThrow();const secure=new Security2();let i=0;await expect(secure.open(fixture.username,'wrong-setup-secret',async()=>bytes(i++===0?fixture.response0:fixture.response1))).rejects.toThrow();await expect(secure.exchange(new Uint8Array(),async v=>v)).rejects.toThrow();});
for(const stage of ['importKey','encrypt','decrypt'] as const)it(`closing setup during ${stage} cannot revive the session or deliver cancelled data`,async()=>{
 const entropy=vi.spyOn(crypto,'getRandomValues').mockImplementation((a:any)=>{a.set(bytes(fixture.secret));return a;});const secure=new Security2();let i=0,entered=false,release=()=>{};
 const gate=new Promise<void>(resolve=>{release=resolve;}),original=crypto.subtle[stage];let interception:ReturnType<typeof vi.spyOn>|undefined;
 const intercept=()=>{interception=vi.spyOn(crypto.subtle,stage).mockImplementation(async(...args:any[])=>{entered=true;await gate;return Reflect.apply(original,crypto.subtle,args);});};
 const exchange=vi.fn(async()=>bytes(fixture.encryptedReply));
 try{
  if(stage==='importKey')intercept();
  const opening=secure.open(fixture.username,fixture.password,async()=>bytes(i++===0?fixture.response0:fixture.response1));
  let operation:Promise<unknown>=opening;
  if(stage!=='importKey'){await opening;intercept();operation=secure.exchange(new TextEncoder().encode('{"op":"scan"}'),exchange);}
  for(let n=0;n<200&&!entered;n++)await new Promise(r=>setTimeout(r,5));expect(entered).toBe(true);secure.close();release();await expect(operation).rejects.toThrow();
  if(stage==='encrypt')expect(exchange).not.toHaveBeenCalled();await expect(secure.exchange(new Uint8Array(),exchange)).rejects.toThrow();
 }finally{release();secure.close();interception?.mockRestore();entropy.mockRestore();}
});
