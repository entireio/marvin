import { randomUUID } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import type { AgentEvent } from '../../../packages/contracts/src/index.js';
import { VoiceControl } from '../../../packages/contracts/src/voice.js';
import type { Runtime } from '../../../packages/runtime/src/runtime.js';
import type { VoiceProvider,VoiceConnection,VoiceSignal } from '../../../packages/runtime/src/voice.js';

export function registerVoice(app:FastifyInstance,runtime:Runtime,origin:string,provider?:VoiceProvider,limits={idleMs:90000,maxMs:900000,heartbeatMs:5000}){
 const owners=new Map<string,()=>void>();
 app.get('/api/voice',{websocket:true},(socket,req)=>{
  if(req.headers.origin!==origin){socket.close(4403,'Invalid origin');return;}
  if(!provider){socket.close(4409,'Voice is not configured');return;}
  let closed=false,initializing=false,muted=false,ownerId='',conversationId='',activeId='',lastId='',connection:VoiceConnection|undefined,lastActivity=Date.now(),inBytes=0,windowAt=Date.now(),frames=0,queued=0;
  let inputEpoch=0;
  const routeId=randomUUID(),seen=new Set<string>();let delivery=Promise.resolve(),control=Promise.resolve();
  const authenticated=async()=>{const s=await runtime.store.session(req.cookies.marvin_session);if(!s||ownerId&&s.ownerId!==ownerId)throw new Error('Sign in again');return s;};
  const send=(data:unknown):Promise<void>=>{if(closed)return Promise.resolve();if(++queued>256){shutdown(1013,'Voice connection is too slow. Reconnect.');return Promise.resolve();}
   delivery=delivery.then(async()=>{if(closed)return;await authenticated();if(closed)return;if(socket.bufferedAmount>262144)throw new Error('Slow connection');socket.send(JSON.stringify({v:1,...data as object}));}).catch(()=>shutdown(4401,'Voice connection ended. Sign in or reconnect.')).finally(()=>{queued--;});return delivery;
  };
  const interrupt=async()=>{const id=activeId||lastId;activeId='';if(id){await runtime.cancel(ownerId,id);await runtime.waitTurn(id);}await send({type:'flush'});};
  const shutdown=(code=1000,reason='Voice ended')=>{if(closed)return;closed=true;clearInterval(heartbeat);clearTimeout(startTimer);clearTimeout(maxTimer);runtime.events.off('event',event);runtime.events.off('refresh',refresh);connection?.close();if(ownerId&&owners.get(ownerId)===closeSession)owners.delete(ownerId);if(activeId)void runtime.cancel(ownerId,activeId).catch(()=>{});socket.close(code,reason.slice(0,100));};
  const closeSession=()=>shutdown(1000,'Voice ended');
  const fault=(message:string)=>{void send({type:'error',message}).then(()=>shutdown(1011,message));};
  const signal=(e:VoiceSignal)=>{
   if(closed)return;if(e.type==='fault'){fault('Voice is unavailable. Your conversation is saved; reconnect or continue in text.');return;}
   if(e.type==='speech_start'){lastActivity=Date.now();void interrupt().then(()=>send({type:'state',state:muted?'muted':'hearing'}));return;}
   if(e.type==='speech_end'){lastActivity=Date.now();void send({type:'state',state:muted?'muted':'thinking'});return;}
   if(e.type==='transcript'){
    const inputGeneration=inputEpoch;
    if(seen.has(e.itemId))return;seen.add(e.itemId);if(seen.size>500){fault('Voice session limit reached. Reconnect to continue.');return;}
    control=control.then(async()=>{
     const stale=()=>closed||muted||inputGeneration!==inputEpoch;
     if(stale()||!connection)return;await authenticated();if(stale())return;await interrupt();if(stale())return;const text=e.text.trim();if(!text){await send({type:'state',state:'listening'});return;}if(text.length>12000){fault('That spoken turn was too long. Try a shorter message.');return;}
     lastActivity=Date.now();const id=randomUUID();
     const ctx={...await runtime.context(ownerId,conversationId,id,routeId),surface:'web_voice' as const};
     if(stale())return;activeId=id;lastId=id;
     await send({type:'turn',interactionId:id,text});if(stale())return;
     await runtime.start(ctx,text,{provider:connection.forTurn(e.itemId),audio:async pcm=>{if(closed||activeId!==id)return;await send({type:'audio',interactionId:id,pcm});}});
     if(stale()){await runtime.cancel(ownerId,id);await runtime.waitTurn(id);}
    }).catch(()=>fault('Voice could not start this response. Stop any other response, then reconnect.'));
   }
  };
  const event=(e:AgentEvent,owner:string)=>{if(closed||owner!==ownerId||e.routeId!==routeId||e.conversationId!==conversationId)return;void send({type:'event',event:e});if(['completed','cancelled','error'].includes(e.type)&&e.interactionId===activeId){activeId='';lastActivity=Date.now();void send({type:'state',state:muted?'muted':'listening'});}};
  const refresh=(id:string,owner:string)=>{if(owner===ownerId&&id===conversationId)void send({type:'refresh'});};
  runtime.events.on('event',event);runtime.events.on('refresh',refresh);
  const heartbeat=setInterval(()=>{if(Date.now()-lastActivity>limits.idleMs){void send({type:'closed',message:'Voice paused after a quiet moment. Reconnect when you’re ready.'}).then(()=>shutdown());return;}void send({type:'heartbeat'});},limits.heartbeatMs);
  const startTimer=setTimeout(()=>{if(!connection)shutdown(4408,'Voice setup timed out');},15000);
  const maxTimer=setTimeout(()=>{void send({type:'closed',message:'Reconnect to start a fresh voice session. Your conversation is saved.'}).then(()=>shutdown());},limits.maxMs);
  socket.on('message',(data,isBinary)=>{
   if(closed)return;const now=Date.now();if(now-windowAt>=1000){windowAt=now;inBytes=0;frames=0;}inBytes+=(data instanceof ArrayBuffer?data.byteLength:Array.isArray(data)?data.reduce((n,b)=>n+b.length,0):data.length);frames++;if(inBytes>100000||frames>120){shutdown(4429,'Audio rate exceeded');return;}
   if(isBinary){if(!connection||muted)return;const pcm=Buffer.from(data as Buffer);if(!pcm.length||pcm.length>4096||pcm.length%2){shutdown(4400,'Invalid audio frame');return;}
    // Each audio packet is authenticated before forwarding, with a bounded queue.
    if(++queued>256){shutdown(1013,'Input connection too slow');return;}
    control=control.then(async()=>{await authenticated();if(!closed&&!muted)connection?.append(pcm);}).catch(()=>shutdown(4401,'Voice connection ended')).finally(()=>{queued--;});return;
   }
   let msg;try{msg=VoiceControl.parse(JSON.parse(data.toString()));}catch{shutdown(4400,'Invalid voice message');return;}
   if(msg.type==='stop'){shutdown();return;}
   if(msg.type==='interrupt'){void interrupt().catch(()=>shutdown());return;}
   if(msg.type==='mute'){muted=msg.muted;lastActivity=now;if(muted){inputEpoch++;try{connection?.clear();}catch{fault('Voice became unavailable while muting. Reconnect or continue in text.');return;}void interrupt().catch(()=>shutdown());}void send({type:'state',state:muted?'muted':'listening'});return;}
   if(msg.type==='ping'){void send({type:'heartbeat'});return;}
   if(msg.type==='start'){
    if(initializing){shutdown(4400,'Voice already started');return;}initializing=true;
    void(async()=>{const s=await authenticated();if(msg.csrf!==s.csrf)throw new Error('Invalid request');await runtime.store.getConversation(s.ownerId,msg.conversationId);if(owners.has(s.ownerId)||owners.size>=32){await send({type:'error',message:'Voice is already open or the server is busy. Close the other voice session and retry.'});shutdown(4409,'Voice busy');return;}
     if(closed)return;ownerId=s.ownerId;conversationId=msg.conversationId;owners.set(ownerId,closeSession);const opened=await provider.connect(signal);if(closed){opened.close();return;}connection=opened;clearTimeout(startTimer);await send({type:'ready',sampleRate:24000,routeId});await send({type:'state',state:'listening'});
    })().catch(()=>fault('Voice could not connect. Check your sign-in and server voice configuration.'));
   }
  });
  socket.on('error',()=>shutdown());socket.on('close',()=>shutdown());
 });
 app.addHook('onClose',async()=>{for(const close of owners.values())close();owners.clear();});
 return {closeAll(){for(const close of owners.values())close();}};
}
