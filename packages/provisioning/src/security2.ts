import {N,G} from './srp-group.js';
const utf8=new TextEncoder();
const cat=(...parts:Uint8Array[])=>{const out=new Uint8Array(parts.reduce((n,p)=>n+p.length,0));let at=0;for(const p of parts){out.set(p,at);at+=p.length;}return out;};
const num=(bytes:Uint8Array)=>BigInt('0x'+(Array.from(bytes,b=>b.toString(16).padStart(2,'0')).join('')||'0'));
const bin=(n:bigint,width=0)=>{let hex=n.toString(16);if(hex.length%2)hex='0'+hex;const bytes=Uint8Array.from(hex.match(/../g)!,x=>parseInt(x,16));if(bytes.length>=width)return bytes;const padded=new Uint8Array(width);padded.set(bytes,width-bytes.length);return padded;};
const hash=async(...p:Uint8Array[])=>new Uint8Array(await crypto.subtle.digest('SHA-512',cat(...p)));
function pow(base:bigint,exponent:bigint){let result=1n;base=(base%N+N)%N;while(exponent){if(exponent&1n)result=result*base%N;base=base*base%N;exponent>>=1n;}return result;}
/** Matches Espressif's SRP6a byte representation, including minimal integer hashes. */
export async function deriveSrp(username:string,password:string,secret:Uint8Array,salt:Uint8Array,serverPublic:Uint8Array){
 if(secret.length!==32||salt.length!==16||serverPublic.length>384||!serverPublic.length)throw new Error('Invalid secure setup challenge.');
 const a=num(secret),A=pow(G,a),B=num(serverPublic);if(a===0n||B<=0n||B>=N)throw new Error('Invalid SRP public value.');
 const k=num(await hash(bin(N,384),bin(G,384))),u=num(await hash(bin(A,384),bin(B,384)));if(!u)throw new Error('Invalid SRP scrambling value.');
 const inner=num(await hash(utf8.encode(username+':'+password))),x=num(await hash(bin(num(salt)),bin(inner)));
 const shared=pow(B-k*pow(G,x),a+u*x),key=await hash(bin(shared));const hn=await hash(bin(N)),hg=await hash(bin(G,384));
 const proof=await hash(Uint8Array.from(hn,(b,i)=>b^hg[i]),await hash(utf8.encode(username)),bin(num(salt)),bin(A),bin(B),key);
 return {publicKey:bin(A),proof,serverProof:await hash(bin(A),proof,key),key:key.slice(0,32)};
}
const vint=(n:number)=>{const bytes=[];do{bytes.push((n&127)|(n>127?128:0));n>>>=7;}while(n);return Uint8Array.from(bytes);};
const field=(id:number,value:number|Uint8Array)=>typeof value==='number'?cat(vint(id*8),vint(value)):cat(vint(id*8+2),vint(value.length),value);
function parse(data:Uint8Array){if(data.length>4096)throw new Error('Secure setup response too large.');const fields=new Map<number,number|Uint8Array>();let at=0;const read=()=>{let value=0;for(let shift=0;shift<28;shift+=7){if(at>=data.length)throw new Error('Truncated setup response.');const b=data[at++];value+=(b&127)*2**shift;if(!(b&128))return value;}throw new Error('Invalid setup integer.');};while(at<data.length){const tag=read(),id=tag>>>3;if(!id||fields.has(id))throw new Error('Duplicate setup field.');if((tag&7)===0)fields.set(id,read());else if((tag&7)===2){const len=read();if(at+len>data.length)throw new Error('Truncated setup field.');fields.set(id,data.slice(at,at+len));at+=len;}else throw new Error('Unsupported setup field.');}return fields;}
function bytes(fields:Map<number,number|Uint8Array>,id:number){const b=fields.get(id);if(!(b instanceof Uint8Array))throw new Error('Missing setup field.');return b;}
function response(data:Uint8Array,msg:number,id:number){const outer=parse(data);if(outer.get(2)!==2)throw new Error('Marvin must use Security 2.');const inner=parse(bytes(outer,12));if(inner.get(1)!==msg)throw new Error('Unexpected setup response.');const result=parse(bytes(inner,id));if((result.get(1)??0)!==0)throw new Error('Setup code was rejected.');return result;}
const packet=(msg:number,id:number,payload:Uint8Array)=>cat(field(2,2),field(12,cat(field(1,msg),field(id,payload))));
export class Security2 {
 private key?:CryptoKey;private nonce=new Uint8Array(12);private busy=false;private failed=false;
 /** patch 1 increments the GCM nonce for every request and response. Never negotiate patch 0. */
 async open(username:string,password:string,exchange:(data:Uint8Array)=>Promise<Uint8Array>){
  if(this.key||this.failed||this.busy)throw new Error('Create a new secure session.');if(!username||username.length>64||password.length<8||password.length>128)throw new Error('Check the setup credentials.');
  this.busy=true;const secret=crypto.getRandomValues(new Uint8Array(32));let derivedKey:Uint8Array|undefined;
  try{const A=bin(pow(G,num(secret)));const first=response(await exchange(packet(0,20,cat(field(1,utf8.encode(username)),field(2,A)))),1,21);const derived=await deriveSrp(username,password,secret,bytes(first,3),bytes(first,2));derivedKey=derived.key;if(this.failed)throw new Error('Secure setup was cancelled.');const last=response(await exchange(packet(2,22,field(1,derived.proof))),3,23),proof=bytes(last,2),nonce=bytes(last,3);
   let mismatch=proof.length^derived.serverProof.length;for(let i=0;i<64;i++)mismatch|=(proof[i]??0)^derived.serverProof[i];if(mismatch||nonce.length!==12)throw new Error('Marvin did not prove the setup secret.');
   if(this.failed)throw new Error('Secure setup was cancelled.');const imported=await crypto.subtle.importKey('raw',new Uint8Array(derived.key),'AES-GCM',false,['encrypt','decrypt']);if(this.failed)throw new Error('Secure setup was cancelled.');this.key=imported;this.nonce.set(nonce);
  }catch(e){this.failed=true;throw e;}finally{secret.fill(0);derivedKey?.fill(0);this.busy=false;}
 }
 private next(){const dv=new DataView(this.nonce.buffer),count=dv.getUint32(8);if(count===0xffffffff)throw new Error('Secure setup session expired.');dv.setUint32(8,count+1);}
 async exchange(clear:Uint8Array,transport:(data:Uint8Array)=>Promise<Uint8Array>){
  if(!this.key||this.failed||this.busy)throw new Error('Secure session is unavailable or busy.');if(clear.length>4096)throw new Error('Setup message too large.');this.busy=true;
  try{const ciphertext=new Uint8Array(await crypto.subtle.encrypt({name:'AES-GCM',iv:new Uint8Array(this.nonce)},this.key,new Uint8Array(clear)));if(this.failed)throw new Error('Secure setup was cancelled.');this.next();const encrypted=await transport(ciphertext);if(this.failed)throw new Error('Secure setup was cancelled.');if(encrypted.length>4112)throw new Error('Setup response too large.');const plain=new Uint8Array(await crypto.subtle.decrypt({name:'AES-GCM',iv:new Uint8Array(this.nonce)},this.key,new Uint8Array(encrypted)));if(this.failed)throw new Error('Secure setup was cancelled.');this.next();return plain;}catch(e){this.close();throw e;}finally{this.busy=false;}
 }
 close(){this.failed=true;this.key=undefined;this.nonce.fill(0);}
}
