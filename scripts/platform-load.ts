/** Local transport/load evidence. Uses synthetic text/PCM only; never reads .env or calls a provider. */
import {parseArgs} from 'node:util';
import {randomUUID} from 'node:crypto';
import {mkdir,writeFile} from 'node:fs/promises';
import {cpus,totalmem} from 'node:os';
import {monitorEventLoopDelay} from 'node:perf_hooks';
import {setTimeout as delay} from 'node:timers/promises';
import WebSocket from 'ws';
import {createApp} from '../apps/server/src/app.js';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
import {FixtureTextProvider} from '../packages/runtime/src/provider.js';
import type {VoiceProvider} from '../packages/runtime/src/voice.js';
import {DeviceSimulator} from '../packages/device/src/simulator.js';
const {values}=parseArgs({options:{seconds:{type:'string',default:'7200'},output:{type:'string',default:'work/m11/load'}}});
const seconds=Number(values.seconds);if(!Number.isInteger(seconds)||seconds<60||seconds>7200)throw new Error('Use 60–7200 seconds.');
const directory=values.output!;await mkdir(directory,{recursive:true});const runId=randomUUID(),origin='http://127.0.0.1:5173';
let activeProviders=0,inputBytes=0,outputBytes=0,requests=0,requestErrors=0,completed=0,voiceTurns=0,stopping=false,failure='';
const latency:number[]=[],samples:unknown[]=[];const bounded=(a:number[],v:number)=>{a.push(v);if(a.length>10000)a.shift();};
const voice:VoiceProvider={async connect(notify){activeProviders++;let closed=false,last=Date.now();return {append(pcm){inputBytes+=pcm.length;if(Date.now()-last>=30000){last=Date.now();voiceTurns++;notify({type:'transcript',itemId:randomUUID(),text:'Synthetic load utterance.'});}},clear(){},close(){if(!closed){closed=true;activeProviders--; }},forTurn(){return {name:'synthetic-load',async *run(){yield {type:'delta',text:'Synthetic response.'};for(let n=0;n<50;n++)yield {type:'audio',pcm:Buffer.alloc(960).toString('base64')};}};}};}};
const service=await createApp(config({NODE_ENV:'test',AUTH_MODE:'local',LOCAL_PASSWORD_HASH:'scrypt$'+'0'.repeat(32)+'$'+'0'.repeat(128),TRUST_PROXY_HOPS:'1'}),{database:sqliteDatabase(`${directory}/${runId}.sqlite`),provider:new FixtureTextProvider(0),voice});
const address=await service.app.listen({host:'127.0.0.1',port:0}),wsAddress=address.replace('http:','ws:');
const sockets=new Set<WebSocket>(),robots:DeviceSimulator[]=[],users:{owner:string;conversation:string;route:string;csrf:string;headers:Record<string,string>}[]=[];
const until=async(check:()=>boolean)=>{for(let n=0;n<500;n++){if(check())return;await delay(10);}throw new Error('Transport readiness timeout');};
async function socket(path:string,headers:Record<string,string>,handler:(event:any)=>void){const ws=new WebSocket(wsAddress+path,{headers});sockets.add(ws);ws.on('close',()=>sockets.delete(ws));ws.on('message',raw=>handler(JSON.parse(raw.toString())));ws.on('error',()=>{if(!stopping)failure='SOCKET_ERROR';});await new Promise<void>((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});return ws;}
const voiceClients:{ws:WebSocket;user:typeof users[number]}[]=[];
async function openVoice(user:typeof users[number]){let ready=false;const ws=await socket('/api/voice',user.headers,e=>{if(e.type==='ready')ready=true;if(e.type==='audio')outputBytes+=Buffer.from(e.pcm,'base64').length;if(e.type==='error')failure='VOICE_ERROR';});ws.send(JSON.stringify({v:1,type:'start',conversationId:user.conversation,csrf:user.csrf}));await until(()=>ready);return ws;}
let started=0,lastSample=0,baseline=0,peak=0;const loop=monitorEventLoopDelay({resolution:20});loop.enable();
for(const sig of ['SIGTERM','SIGINT'] as const)process.on(sig,()=>{stopping=true;failure='INTERRUPTED';});
try{
 for(let n=0;n<100;n++){
  const owner=(await service.store.ensureOwner('load',String(n),'Load fixture')).id,session=await service.store.createSession(owner),conversation=(await service.store.createConversation(owner)).id,route=randomUUID();
  const user={owner,conversation,route,csrf:session.csrf,headers:{cookie:'marvin_session='+session.token,origin,'x-forwarded-for':`192.0.2.${n+1}`}};users.push(user);
  let subscribed=false;const ws=await socket('/api/events',user.headers,e=>{if(e.type==='subscribed')subscribed=true;if(e.type==='completed')completed++;});ws.send(JSON.stringify({v:1,type:'subscribe',conversationId:conversation,routeId:route,after:0}));await until(()=>subscribed);
  const device=randomUUID(),enrollment=randomUUID();await service.store.reserve(owner,device,enrollment,true);await service.store.redeem(owner,device,enrollment,'Fixture network');const token=await service.devices.persistence.issue(owner,device),robot=new DeviceSimulator(device);await robot.connect(wsAddress+'/api/device/socket',token);robots.push(robot);
 }
 // Voice uses separate conversations so text and voice never compete for the same generation lock.
 for(const user of users.slice(0,20)){const vuser={...user,conversation:(await service.store.createConversation(user.owner)).id};voiceClients.push({user:vuser,ws:await openVoice(vuser)});}
 started=Date.now();let lastWork=0,lastVoiceRenewal=started;const pcm=Buffer.alloc(960);
 while(!stopping&&Date.now()-started<seconds*1000){
  const now=Date.now();if(failure)throw new Error(failure);
  if(now-lastVoiceRenewal>840000){for(const c of voiceClients){c.ws.send(JSON.stringify({v:1,type:'stop'}));await until(()=>c.ws.readyState===WebSocket.CLOSED);c.ws=await openVoice(c.user);}lastVoiceRenewal=Date.now();}
  for(const c of voiceClients){if(c.ws.readyState!==WebSocket.OPEN)throw new Error('VOICE_DISCONNECTED');c.ws.send(pcm);}
  if(now-lastWork>=60000){lastWork=now;await Promise.all(users.map(async user=>{const at=Date.now();requests++;try{const response=await fetch(address+'/api/turns',{method:'POST',headers:{...user.headers,'content-type':'application/json','x-csrf-token':user.csrf},body:JSON.stringify({conversationId:user.conversation,interactionId:randomUUID(),routeId:user.route,text:'Synthetic load message.'})});await response.arrayBuffer();if(response.status!==202)requestErrors++;bounded(latency,Date.now()-at);}catch{requestErrors++;}}));}
  if(now-lastSample>=10000){lastSample=now;const m=process.memoryUsage(),elapsed=Math.floor((now-started)/1000);if(elapsed>=300)baseline||=m.rss;if(baseline)peak=Math.max(peak,m.rss);const connected=users.filter(u=>service.devices.presence(u.owner)).length;if(connected!==100||activeProviders!==20||sockets.size!==120)throw new Error('CONCURRENCY_DROPPED');samples.push({elapsedSeconds:elapsed,rssBytes:m.rss,heapBytes:m.heapUsed,deviceConnections:connected,browserConnections:sockets.size,providerSessions:activeProviders,eventLoopP99Ms:loop.percentile(99)/1e6});loop.reset();await report('running');}
  await delay(20);
 }
}catch(e){failure=e instanceof Error?e.message:'LOAD_FAILED';}
finally{stopping=true;for(const ws of sockets)ws.terminate();for(const robot of robots)robot.close();await service.app.close();loop.disable();await report(failure?'failed':'finished');}
async function report(status:string){const sorted=[...latency].sort((a,b)=>a-b),elapsed=started?Math.floor((Date.now()-started)/1000):0;const result={runId,pid:process.pid,status,startedAt:started?new Date(started).toISOString():null,elapsedSeconds:elapsed,targetSeconds:seconds,failures:failure?[failure]:[],requests,requestErrors,errorRatio:requests?requestErrors/requests:null,completedTextTurns:completed,voiceTurns,syntheticInputBytes:inputBytes,syntheticOutputBytes:outputBytes,deviceUploadedAudioBytes:robots.reduce((n,r)=>n+r.uploadedAudioBytes,0),httpAcceptanceP95Ms:sorted[Math.floor(sorted.length*.95)]??null,baselineRssBytes:baseline||null,peakRssBytesAfterWarmup:peak||null,growthRatio:baseline?(peak-baseline)/baseline:null,host:{platform:process.platform,arch:process.arch,node:process.version,cpus:cpus().length,ramBytes:totalmem()},scope:'100 authenticated browser event sockets, 100 idle device sockets, 20 voice sockets at synthetic 24kHz PCM; 100 text submissions/minute. Server and load clients share this process. No real providers, microphone, credentials, or repository data. HTTP duration measures request acceptance, not model/audio latency.',acceptance:status==='finished'&&seconds===7200&&elapsed>=7200&&!failure&&requestErrors/requests<.01?'synthetic transport workload passed; M11 real-provider gates remain open':'not accepted',samples};await writeFile(`${directory}/results.json`,JSON.stringify(result,null,2));if(status!=='running')console.log(JSON.stringify({...result,samples:undefined}));}
