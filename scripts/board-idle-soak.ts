/** Isolated LAN-board observation. No voice activation, reset, or audio stimulus. */
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {request} from 'node:https';
import {parseArgs} from 'node:util';
import {z} from 'zod';
const {values}=parseArgs({options:{seconds:{type:'string',default:'3600'}}});
const seconds=z.coerce.number().int().min(30).max(28800).parse(values.seconds);
const ca=readFileSync('work/board/board-backend-root.crt');
const credentials=z.object({password:z.string()}).parse(JSON.parse(readFileSync('work/board/backend-login.json','utf8')));
const diagnostic=z.object({bootId:z.string(),connectionOpenedAt:z.number(),audioReceivedBytes:z.number(),audioSentBytes:z.number(),voiceStarts:z.number(),voiceActive:z.boolean()});
let cookie='';
async function api(path:string,body?:unknown):Promise<any>{
 const data=body===undefined?null:Buffer.from(JSON.stringify(body));
 return new Promise((resolve,reject)=>{
  const req=request(new URL(path,'https://192.168.1.5:8443'),{ca,method:data?'POST':'GET',timeout:10000,headers:{cookie,...(data?{'content-type':'application/json','content-length':String(data.length),'origin':'https://192.168.1.5:8443'}:{})}},res=>{
   const chunks:Buffer[]=[];let bytes=0;
   res.on('data',chunk=>{bytes+=chunk.length;if(bytes>65536){req.destroy();reject(new Error('Unexpected response size'));}else chunks.push(chunk);});
   res.on('end',()=>{if(res.statusCode!==200){reject(new Error('Board test API request failed'));return;}if(res.headers['set-cookie'])cookie=res.headers['set-cookie'][0].split(';')[0];try{resolve(JSON.parse(Buffer.concat(chunks).toString()));}catch{reject(new Error('Invalid response'));}});
  });req.on('timeout',()=>req.destroy(new Error('Board test API timed out')));req.on('error',()=>reject(new Error('Board test API transport failed')));req.end(data);
 });
}
await api('/api/auth/login',credentials);credentials.password='';
const baseline=diagnostic.parse((await api('/api/robot/diagnostics')).diagnostics);
if(baseline.voiceActive)throw new Error('Stop the explicit voice session before idle observation.');
const started=Date.now(),directory=`work/board/idle-${started}`;mkdirSync(directory,{recursive:true,mode:0o700});
let samples=0,failures=0,last:unknown=null,complete=false;
function save(){writeFileSync(directory+'/results.json',JSON.stringify({at:new Date().toISOString(),requestedSeconds:seconds,elapsedSeconds:(Date.now()-started)/1000,complete,passed:complete&&failures===0,samples,failures,baseline,last,scope:'Authenticated application-layer physical device counters and provider-session state. Continuous connection required; complements but does not replace packet capture or acoustic tests.'},null,2),{mode:0o600});}
try{
 while(Date.now()-started<seconds*1000){
  await new Promise(resolve=>setTimeout(resolve,Math.min(5000,seconds*1000-(Date.now()-started))));
  try{const now=diagnostic.parse((await api('/api/robot/diagnostics')).diagnostics);last=now;samples++;
   if(now.voiceActive||now.bootId!==baseline.bootId||now.connectionOpenedAt!==baseline.connectionOpenedAt||now.audioReceivedBytes!==baseline.audioReceivedBytes||now.audioSentBytes!==baseline.audioSentBytes||now.voiceStarts!==baseline.voiceStarts)failures++;
  }catch{failures++;last={error:'Observation unavailable'};}
  save();
 }
 complete=true;
}finally{save();}
console.log(JSON.stringify({directory,complete,passed:complete&&failures===0,samples,failures}));
process.exitCode=failures?1:0;
