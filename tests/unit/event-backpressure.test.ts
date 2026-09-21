import {it,expect} from 'vitest';
import WebSocket from 'ws';
import {randomUUID} from 'node:crypto';
import {createApp} from '../../apps/server/src/app.js';
import {config} from '../../apps/server/src/config.js';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {FixtureTextProvider} from '../../packages/runtime/src/provider.js';
const until=async(check:()=>boolean)=>{for(let n=0;n<200;n++){if(check())return;await new Promise(r=>setTimeout(r,10));}throw new Error('Timed out');};
for(const kind of ['output','subscriptions'])it(`bounds ${kind} while session storage is stalled and removes socket listeners`,async()=>{
 const service=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(),provider:new FixtureTextProvider(0)});
 const owner=(await service.store.ensureOwner('test',randomUUID(),'Fixture')).id,session=await service.store.createSession(owner),conversation=await service.store.createConversation(owner),route=randomUUID();
 const address=(await service.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:');
 const ws=new WebSocket(address+'/api/events',{headers:{origin:'http://127.0.0.1:5173',cookie:'marvin_session='+session.token}});let subscribed=false,code=0,release=()=>{};
 ws.on('message',raw=>{if(JSON.parse(raw.toString()).type==='subscribed')subscribed=true;});ws.on('close',c=>{code=c;});
 const original=service.store.session.bind(service.store);
 try{
  await new Promise<void>((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});ws.send(JSON.stringify({v:1,type:'subscribe',conversationId:conversation.id,routeId:route,after:0}));await until(()=>subscribed);const baseline=service.runtime.events.listenerCount('event')-1;
  const stalled=new Promise<void>(resolve=>{release=resolve;});service.store.session=async(...args)=>{await stalled;return original(...args);};
  for(let n=0;n<400;n++){
   if(kind==='output')service.runtime.events.emit('event',{type:'delta',conversationId:conversation.id,routeId:route,text:'fixture'},owner);
   else ws.send(JSON.stringify({v:1,type:'ping'}));
  }
  await until(()=>code!==0);expect(code).toBe(1013);await until(()=>service.runtime.events.listenerCount('event')===baseline);
 }finally{release();service.store.session=original;ws.terminate();await service.app.close();}
});
