import { it,expect,beforeAll,afterAll } from 'vitest';
import { WebSocketServer } from 'ws';
import { createServer } from 'node:https';
import { mkdtempSync,readFileSync,rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
let dir:string,ca:string,key:string;
beforeAll(()=>{dir=mkdtempSync(join(tmpdir(),'marvin-voice-tls-'));execFileSync('openssl',['req','-x509','-newkey','rsa:2048','-nodes','-keyout',join(dir,'key.pem'),'-out',join(dir,'cert.pem'),'-days','1','-subj','/CN=localhost','-addext','subjectAltName=IP:127.0.0.1,DNS:localhost'],{stdio:'ignore'});ca=readFileSync(join(dir,'cert.pem'),'utf8');key=readFileSync(join(dir,'key.pem'),'utf8');});
afterAll(()=>rmSync(dir,{recursive:true,force:true}));
async function tlsServer(){const http=createServer({key,cert:ca});const ws=new WebSocketServer({server:http});await new Promise<void>(r=>http.listen(0,'127.0.0.1',r));return {ws,http};}

import { OpenAIVoiceProvider } from '../../packages/runtime/src/openai-voice.js';
import type { ModelContext } from '../../packages/runtime/src/provider.js';
const context:ModelContext={interaction:{ownerId:'test',conversationId:'thread',interactionId:'turn',routeId:'voice-route',surface:'web_voice',repositoryId:'marvin-firmware',entireState:'fixture',body:{deviceId:'robot',status:'online',capabilities:['head']}},summary:'Earlier: calibration value 42.',messages:[{userText:'Remember the blue light',assistantText:'I will remember.',status:'completed'}],input:'What changed?'};
for(const surface of ['web_voice','body_voice'] as const)it(`real SDK configures PCM/VAD and scopes reconstructed context/tools to ${surface}`,async()=>{
 const scoped={...context,interaction:{...context.interaction,surface,routeId:surface==='body_voice'?'robot':context.interaction.routeId}};
 const {ws:server,http}=await tlsServer();const port=(http.address() as {port:number}).port;const requests:any[]=[];
 server.on('connection',(ws,req)=>{expect(req.headers.authorization).toBe('Bearer fixture-key');ws.on('message',raw=>{const e=JSON.parse(raw.toString());requests.push(e);const send=(event:object)=>ws.send(JSON.stringify({event_id:'fixture',...event}));
  if(e.type==='session.update')send({type:'session.updated',session:{type:'realtime'}});
  if(e.type==='response.create'){const round=requests.filter(e=>e.type==='response.create').length;const response={id:'response-'+round,metadata:e.response.metadata,status:'completed',output:round===1?[{type:'function_call',id:'fc',call_id:'call',name:'repository_summary',arguments:'{}'}]:[]};send({type:'response.created',response});if(round===2){send({type:'response.output_audio.delta',response_id:response.id,delta:Buffer.alloc(960).toString('base64')});send({type:'response.output_audio_transcript.delta',response_id:response.id,delta:'Grounded spoken answer.'});}send({type:'response.done',response});}
 });});
 const provider=new OpenAIVoiceProvider('fixture-key','test-model','test-transcribe','marin',`https://127.0.0.1:${port}/v1`,ca);const connection=await provider.connect(()=>{});
 try{const events=[];const calls:string[]=[];for await(const e of connection.forTurn('audio-item').run(scoped,async name=>{calls.push(name);return {kind:'repository',repositoryId:'marvin-firmware',source:'fixture',title:'Fixture',summary:'Recorded evidence',revision:'test',files:[]};},AbortSignal.timeout(3000)))events.push(e);
 expect(calls).toEqual(['repository_summary']);expect(events.some(e=>e.type==='audio')).toBe(true);expect(events.some(e=>e.type==='delta'&&e.text==='Grounded spoken answer.')).toBe(true);expect(events.some(e=>e.type==='card')).toBe(true);
 const setup=requests.find(e=>e.type==='session.update').session;expect(setup.audio.input.turn_detection).toMatchObject({create_response:false,interrupt_response:false});expect(setup.audio.input.format).toEqual({type:'audio/pcm',rate:24000});
 const first=requests.find(e=>e.type==='response.create').response;expect(first.conversation).toBe('none');expect(first.input.at(-1)).toEqual({type:'item_reference',id:'audio-item'});expect(JSON.stringify(first.input)).toContain('blue light');expect(JSON.stringify(first.input)).toContain('calibration value 42');expect(first.instructions).toContain('Surface: '+surface+'.');expect(first.tools.map((t:any)=>t.name)).toEqual(surface==='body_voice'?['repository_summary','physical_head']:['repository_summary']);expect(requests.filter(e=>e.type==='response.create')[1].response.input.at(-1).type).toBe('function_call_output');
 }finally{connection.close();for(const ws of server.clients)ws.terminate();await new Promise<void>(r=>server.close(()=>http.close(()=>r())));}
});
it('provider failures are sanitized and close a failed handshake',async()=>{const {ws:server,http}=await tlsServer();server.on('connection',ws=>ws.on('message',()=>ws.send(JSON.stringify({type:'error',event_id:'fixture',error:{type:'invalid_request_error',code:'insufficient_quota',message:'private provider diagnostic'}}))));const signals:unknown[]=[];try{await expect(new OpenAIVoiceProvider('fixture','test','test','marin',`https://127.0.0.1:${(http.address() as {port:number}).port}/v1`,ca).connect(e=>signals.push(e))).rejects.toMatchObject({code:'VOICE_UNAVAILABLE'});expect(JSON.stringify(signals)).not.toContain('private provider diagnostic');}finally{for(const ws of server.clients)ws.terminate();await new Promise<void>(r=>server.close(()=>http.close(()=>r())));}});

for(const timing of ['generating','completed','awaiting-created'] as const)it(`interruption cancels once only when remote response is still ${timing}`,async()=>{
 const {ws:server,http}=await tlsServer();const requests:any[]=[];const signals:unknown[]=[];
 let release:()=>void=()=>{};let created:()=>void=()=>{};const requested=new Promise<void>(r=>created=r);
 server.on('connection',ws=>ws.on('message',raw=>{const e=JSON.parse(raw.toString());requests.push(e);const send=(event:object)=>ws.send(JSON.stringify({event_id:'fixture',...event}));
  if(e.type==='session.update')send({type:'session.updated',session:{type:'realtime'}});
  if(e.type==='response.create'){
   const response={id:'response',metadata:e.response.metadata,status:'completed',output:[]};
   release=()=>{send({type:'response.created',response});send({type:'response.output_audio.delta',response_id:'response',delta:Buffer.alloc(24000).toString('base64')});if(timing==='completed')send({type:'response.done',response});};
   if(timing!=='awaiting-created')release();created();
  }
 }));
 const connection=await new OpenAIVoiceProvider('fixture','test','test','marin',`https://127.0.0.1:${(http.address() as {port:number}).port}/v1`,ca).connect(e=>signals.push(e));
 try{
  const abort=new AbortController();const iterator=connection.forTurn('item').run(context,async()=>({}),abort.signal)[Symbol.asyncIterator]();
  const first=iterator.next();await requested;
  if(timing!=='awaiting-created'){expect((await first).value?.type).toBe('audio');await new Promise(r=>setTimeout(r,30));}
  abort.abort();
  if(timing==='awaiting-created'){await expect(first).rejects.toThrow();release();}
  else await expect(iterator.next()).rejects.toThrow();
  await new Promise(r=>setTimeout(r,30));
  expect(requests.filter(e=>e.type==='response.cancel')).toHaveLength(timing==='completed'?0:1);
  expect(signals).toEqual([]);
 }finally{connection.close();for(const ws of server.clients)ws.terminate();await new Promise<void>(r=>server.close(()=>http.close(()=>r())));}
});
