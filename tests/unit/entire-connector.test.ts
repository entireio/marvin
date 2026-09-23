import {once} from 'node:events';
import {describe,expect,it} from 'vitest';
import WebSocket,{WebSocketServer} from 'ws';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';
import {EntireConnectorBroker} from '../../apps/server/src/entire-connector.js';

describe('Entire connector relay',()=>{
 it('consumes a pairing code and relays only typed repository operations',async()=>{const db=sqliteDatabase(),server=new WebSocketServer({port:0});try{await migrate(db);const store=new Store(db),owner=await store.ensureOwner('test','connector-owner','Owner'),pairing=await store.createEntireConnectorPairing(owner.id),broker=new EntireConnectorBroker(store);server.on('connection',socket=>broker.attach(socket));await once(server,'listening');const address=server.address();if(typeof address==='string'||address===null)throw new Error('Missing test server address');const client=new WebSocket(`ws://127.0.0.1:${address.port}`),ready=new Promise<void>((resolve,reject)=>{client.on('error',reject);client.on('message',raw=>{const message=JSON.parse(raw.toString());if(message.type==='ready')resolve();if(message.type==='request'){const value=['configured','connect'].includes(message.operation)?[{id:'entire_test',name:'acme/widget',description:'Test repository',source:'entire',capabilities:['summary']}]:message.operation==='check'?{id:'entire_test'}:null;client.send(JSON.stringify({v:1,type:'result',requestId:message.requestId,ok:true,value}));}});});await once(client,'open');client.send(JSON.stringify({v:1,type:'pair',token:pairing.token,name:'Test Mac'}));await ready;await expect(store.consumeEntireConnectorPairing(pairing.token)).resolves.toBeNull();await expect(broker.connect(owner.id)).resolves.toMatchObject([{id:'entire_test',name:'acme/widget'}]);expect(await broker.online(owner.id)).toBe(true);broker.disconnect(owner.id);client.close();}finally{await new Promise<void>(resolve=>server.close(()=>resolve()));await db.close();}});
});
