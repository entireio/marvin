import { beforeEach,afterEach,it,expect } from 'vitest';
import { randomUUID } from 'node:crypto';
import WebSocket from 'ws';
import { createApp } from '../../apps/server/src/app.js';
import { config } from '../../apps/server/src/config.js';
import { sqliteDatabase } from '../../packages/persistence/src/database.js';
import { FixtureTextProvider,type ModelContext,type ModelEvent,type TextProvider } from '../../packages/runtime/src/provider.js';
import type { VoiceProvider,VoiceSignal } from '../../packages/runtime/src/voice.js';
import { allowedTools } from '../../packages/runtime/src/policy.js';
const origin='http://127.0.0.1:5173';
let service:Awaited<ReturnType<typeof createApp>>,owner:string,cookie:string,csrf:string,address:string,notify:(e:VoiceSignal)=>void,closed:number,inputBytes:number,contexts:ModelContext[],sockets:WebSocket[],run:TextProvider['run'],clearFailure:boolean;
const wait=async(check:()=>boolean)=>{for(let i=0;i<300;i++){if(check())return;await new Promise(r=>setTimeout(r,5));}throw new Error('Condition timed out');};
beforeEach(async()=>{
 closed=0;clearFailure=false;inputBytes=0;contexts=[];sockets=[];
 run=async function*(ctx,_execute,signal){contexts.push(ctx);signal.throwIfAborted();yield {type:'delta',text:'Recorded voice response.'};for(let n=0;n<4;n++)yield {type:'audio',pcm:Buffer.alloc(960).toString('base64')};};
 const voice:VoiceProvider={async connect(callback){notify=callback;return {append(pcm){inputBytes+=pcm.length;},clear(){if(clearFailure)throw new Error('Simulated provider close race');},close(){closed++;},forTurn(){return {name:'recorded-voice',run:(...args)=>run(...args)};}};}};
 service=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(),provider:new FixtureTextProvider(0),voice});
 const login=await service.app.inject({method:'POST',url:'/api/auth/login',headers:{origin},payload:{}});cookie=login.cookies[0].name+'='+login.cookies[0].value;csrf=login.json().csrf;owner=login.json().owner.id;address=(await service.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:');
});
afterEach(async()=>{for(const s of sockets)s.terminate();await service.app.close();});
async function open(path='/api/voice',headers={cookie,origin}){const ws=new WebSocket(address+path,{headers});sockets.push(ws);const messages:any[]=[];ws.on('message',data=>messages.push(JSON.parse(data.toString())));await new Promise<void>((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});return {ws,messages};}
async function start(conversationId:string){const c=await open();c.ws.send(JSON.stringify({v:1,type:'start',conversationId,csrf}));await wait(()=>c.messages.some(e=>e.type==='ready'));return c;}
it('twenty text–voice–text journeys reconstruct persisted context across fresh provider sessions',async()=>{
 for(let n=0;n<20;n++){
  const c=await service.store.createConversation(owner);const remembered=`Iteration ${n}: the calibration target is ${71+n}.`;
  await service.runtime.start(await service.runtime.context(owner,c.id,randomUUID(),'text'),remembered);await service.runtime.drain();
  const socket=await start(c.id);notify({type:'transcript',itemId:`input-${n}`,text:'Remember the calibration target.'});await wait(()=>socket.messages.some(e=>e.type==='event'&&e.event.type==='completed'));await service.runtime.drain();
  const ctx=contexts.at(-1)!;expect(ctx.messages.some(m=>m.userText===remembered)).toBe(true);expect(ctx.interaction.surface).toBe('web_voice');expect(allowedTools(ctx.interaction).every(t=>!t.name.startsWith('physical_'))).toBe(true);
  socket.ws.send(JSON.stringify({v:1,type:'stop'}));await wait(()=>socket.ws.readyState===WebSocket.CLOSED);
  await service.runtime.start(await service.runtime.context(owner,c.id,randomUUID(),'text'),'Remember my previous message.');await service.runtime.drain();const turns=await service.store.turns(owner,c.id);expect(turns.map(t=>t.surface)).toEqual(['web_text','web_voice','web_text']);expect(turns[1].assistantText).toBe('Recorded voice response.');expect(turns[2].assistantText).toContain('Remember the calibration target.');
 }
 expect(closed).toBe(20);
});
it('100 routed voice interactions share transcript text without exposing audio to another browser or body channel',async()=>{
 const c=await service.store.createConversation(owner);await service.store.reserve(owner,'robot','enrollment',true);await service.store.redeem(owner,'robot','enrollment','Network');const body=await service.store.body(owner);
 const observer=await open('/api/events');observer.ws.send(JSON.stringify({v:1,type:'subscribe',conversationId:c.id,routeId:'other-browser',after:0}));await wait(()=>observer.messages.some(e=>e.type==='subscribed'));
 const voice=await start(c.id);let broadcastAudio=0;const observe=(e:ModelEvent)=>{if(e.type==='audio')broadcastAudio++;};service.runtime.events.on('event',observe);
 for(let n=0;n<100;n++){notify({type:'transcript',itemId:`route-${n}`,text:`Recorded turn ${n}`});await wait(()=>voice.messages.filter(e=>e.type==='event'&&e.event.type==='completed').length===n+1);}
 expect(voice.messages.filter(e=>e.type==='audio')).toHaveLength(400);expect(observer.messages.some(e=>e.type==='audio')).toBe(false);expect(observer.messages.some(e=>e.type==='delta')).toBe(true);expect(broadcastAudio).toBe(0);expect(await service.store.body(owner)).toEqual(body);
 const replay=await service.store.replay(owner,c.id,voice.messages.find(e=>e.type==='ready').routeId);expect(replay.some(e=>'pcm'in e)).toBe(false);service.runtime.events.off('event',observe);
});
it('barge-in fences late audio and preserves partial transcript without blocking the next turn',async()=>{
 let continueRun:()=>void=()=>{};run=async function*(ctx){contexts.push(ctx);yield {type:'delta',text:'Partial answer'};yield {type:'audio',pcm:Buffer.alloc(960).toString('base64')};await new Promise<void>(r=>{continueRun=r;});yield {type:'audio',pcm:Buffer.alloc(960).toString('base64')};yield {type:'delta',text:' must not persist'};};
 const c=await service.store.createConversation(owner),voice=await start(c.id);notify({type:'transcript',itemId:'first',text:'Start speaking.'});await wait(()=>voice.messages.some(e=>e.type==='audio'));voice.ws.send(JSON.stringify({v:1,type:'interrupt'}));await wait(()=>!contexts.length||!!contexts[0]);await new Promise(r=>setTimeout(r,10));continueRun();await service.runtime.drain();expect(voice.messages.filter(e=>e.type==='audio')).toHaveLength(1);const turn=(await service.store.turns(owner,c.id))[0];expect(turn.status).toBe('cancelled');expect(turn.assistantText).toBe('Partial answer');
});
it('muting drops audio; closing voice frees provider resources and ordinary text remains usable',async()=>{
 const c=await service.store.createConversation(owner),voice=await start(c.id);voice.ws.send(Buffer.alloc(960));await wait(()=>inputBytes===960);voice.ws.send(JSON.stringify({v:1,type:'mute',muted:true}));voice.ws.send(Buffer.alloc(960));await wait(()=>voice.messages.some(e=>e.type==='state'&&e.state==='muted'));await new Promise(r=>setTimeout(r,10));expect(inputBytes).toBe(960);voice.ws.send(JSON.stringify({v:1,type:'stop'}));await wait(()=>closed===1);await service.runtime.start(await service.runtime.context(owner,c.id,randomUUID(),'text'),'Hello');await service.runtime.drain();expect((await service.store.turns(owner,c.id))[0].status).toBe('completed');
});
it('revoked login prevents input forwarding and output audio',async()=>{
 const c=await service.store.createConversation(owner),voice=await start(c.id);await service.store.logout(cookie.split('=')[1]);voice.ws.send(Buffer.alloc(960));await wait(()=>voice.ws.readyState===WebSocket.CLOSED);expect(inputBytes).toBe(0);expect(voice.messages.some(e=>e.type==='audio')).toBe(false);expect(closed).toBe(1);
});
it('forged surface, missing CSRF and foreign conversation cannot open a provider session',async()=>{
 const other=await service.store.ensureOwner('test','other','Other'),foreign=await service.store.createConversation(other.id),own=await service.store.createConversation(owner);
 for(const payload of [{v:1,type:'start',conversationId:own.id,csrf,surface:'body_voice'},{v:1,type:'start',conversationId:own.id,csrf:'wrong'},{v:1,type:'start',conversationId:foreign.id,csrf}]){const c=await open();c.ws.send(JSON.stringify(payload));await wait(()=>c.ws.readyState===WebSocket.CLOSED);}
 expect(closed).toBe(0);
});
it('provider failure closes voice while preserving conversation and permitting text',async()=>{
 const c=await service.store.createConversation(owner),voice=await start(c.id);notify({type:'fault',code:'provider-secret-error-not-exposed'});await wait(()=>voice.ws.readyState===WebSocket.CLOSED);expect(JSON.stringify(voice.messages)).not.toContain('provider-secret-error');await service.runtime.start(await service.runtime.context(owner,c.id,randomUUID(),'text'),'Still here');await service.runtime.drain();expect((await service.store.turns(owner,c.id))[0].status).toBe('completed');
});
it('one owner cannot open simultaneous microphone routes',async()=>{const c=await service.store.createConversation(owner);await start(c.id);const second=await open();second.ws.send(JSON.stringify({v:1,type:'start',conversationId:c.id,csrf}));await wait(()=>second.ws.readyState===WebSocket.CLOSED);expect(second.messages.some(e=>e.type==='ready'||e.type==='audio')).toBe(false);});
it('voice configuration is explicit and requires server credentials and model',()=>{expect(()=>config({VOICE_PROVIDER:'openai'})).toThrow();expect(config({VOICE_PROVIDER:'openai',OPENAI_API_KEY:'test',OPENAI_REALTIME_MODEL:'test-model'}).VOICE_PROVIDER).toBe('openai');});
it('idle timeout closes provider and transport without deleting the conversation',async()=>{
 let releases=0;const local=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(),provider:new FixtureTextProvider(0),voice:{async connect(){return {append(){},clear(){if(clearFailure)throw new Error('Simulated provider close race');},close(){releases++;},forTurn(){return new FixtureTextProvider(0);}};}},voiceLimits:{idleMs:70,maxMs:1000,heartbeatMs:10}});
 const account=await local.store.ensureOwner('test','idle','Idle'),session=await local.store.createSession(account.id),c=await local.store.createConversation(account.id);const url=(await local.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:');const ws=new WebSocket(url+'/api/voice',{headers:{cookie:'marvin_session='+session.token,origin}});const events:any[]=[];ws.on('message',data=>events.push(JSON.parse(data.toString())));
 try{await new Promise<void>(r=>ws.once('open',r));ws.send(JSON.stringify({v:1,type:'start',conversationId:c.id,csrf:session.csrf}));await wait(()=>ws.readyState===WebSocket.CLOSED);expect(events.some(e=>e.type==='closed')).toBe(true);expect(releases).toBe(1);expect((await local.store.getConversation(account.id,c.id)).id).toBe(c.id);}finally{ws.terminate();await local.app.close();}
});
it('voice repository tools share text authorization and reject physical or write calls',async()=>{
 const c=await service.store.createConversation(owner);await service.store.setEntireState(owner,'fixture');await service.store.setRepository(owner,c.id,'marvin-firmware');
 run=async function*(ctx,execute){contexts.push(ctx);await expect(execute('physical_head',{})).rejects.toMatchObject({code:'TOOL_FORBIDDEN'});await expect(execute('repository_write',{})).rejects.toMatchObject({code:'TOOL_FORBIDDEN'});const card=await execute('repository_summary',{}) as any;yield {type:'card',card};yield {type:'delta',text:'Recorded repository voice answer.'};};
 const voice=await start(c.id);notify({type:'transcript',itemId:'repository',text:'Explain this repository.'});await wait(()=>voice.messages.some(e=>e.type==='event'&&e.event.type==='completed'));const turn=(await service.store.turns(owner,c.id))[0];expect(turn.cards[0].source).toBe('fixture');expect(turn.surface).toBe('web_voice');
});
it('malformed PCM and duplicate transcription events cannot produce duplicated turns',async()=>{const c=await service.store.createConversation(owner),voice=await start(c.id);notify({type:'transcript',itemId:'same-item',text:'One utterance'});notify({type:'transcript',itemId:'same-item',text:'One utterance'});await wait(()=>voice.messages.some(e=>e.type==='event'&&e.event.type==='completed'));expect(await service.store.turns(owner,c.id)).toHaveLength(1);voice.ws.send(Buffer.alloc(3));await wait(()=>voice.ws.readyState===WebSocket.CLOSED);expect(inputBytes).toBe(0);});

it('provider clear failure closes voice safely while text stays available',async()=>{const c=await service.store.createConversation(owner),voice=await start(c.id);clearFailure=true;voice.ws.send(JSON.stringify({v:1,type:'mute',muted:true}));await wait(()=>voice.ws.readyState===WebSocket.CLOSED);expect(closed).toBe(1);expect(voice.messages.some(e=>e.type==='error')).toBe(true);await service.runtime.start(await service.runtime.context(owner,c.id,randomUUID(),'text'),'Hello');await service.runtime.drain();expect((await service.store.turns(owner,c.id))[0].status).toBe('completed');});
it('mute fences a transcript waiting for asynchronous context reconstruction',async()=>{const c=await service.store.createConversation(owner),voice=await start(c.id),original=service.runtime.context.bind(service.runtime);let entered=false,release!:()=>void;const gate=new Promise<void>(resolve=>{release=resolve;});service.runtime.context=async(...args)=>{entered=true;await gate;return original(...args);};try{notify({type:'transcript',itemId:'before-mute',text:'This must not generate after muting.'});await wait(()=>entered);voice.ws.send(JSON.stringify({v:1,type:'mute',muted:true}));await wait(()=>voice.messages.some(e=>e.type==='state'&&e.state==='muted'));release();await new Promise(resolve=>setTimeout(resolve,30));await service.runtime.drain();expect(contexts).toHaveLength(0);expect(voice.messages.some(e=>e.type==='audio'||e.type==='turn')).toBe(false);expect(await service.store.turns(owner,c.id)).toHaveLength(0);}finally{release();service.runtime.context=original;}});
it('mute cancels a turn whose database admission completes afterward',async()=>{const c=await service.store.createConversation(owner),voice=await start(c.id),original=service.store.begin.bind(service.store);let entered=false,release!:()=>void;const gate=new Promise<void>(resolve=>{release=resolve;});service.store.begin=async(...args)=>{entered=true;await gate;return original(...args);};try{notify({type:'transcript',itemId:'delayed-admission',text:'Cancel this delayed turn.'});await wait(()=>entered);voice.ws.send(JSON.stringify({v:1,type:'mute',muted:true}));await wait(()=>voice.messages.some(e=>e.type==='state'&&e.state==='muted'));release();await new Promise(resolve=>setTimeout(resolve,30));await service.runtime.drain();expect(voice.messages.some(e=>e.type==='audio')).toBe(false);const turns=await service.store.turns(owner,c.id);expect(turns).toHaveLength(1);expect(turns[0].status).toBe('cancelled');}finally{release();service.store.begin=original;}});
