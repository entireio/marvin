import {it,expect} from 'vitest';
import {randomUUID} from 'node:crypto';
import {sqliteDatabase,postgresDatabase} from '../../packages/persistence/src/database.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';
import {DeviceStore} from '../../packages/device/src/store.js';
for(const mode of ['sqlite',...(process.env.TEST_DATABASE_URL?['postgres']:[])])it(`100 concurrent commands cannot exceed the pending limit: ${mode}`,async()=>{const db=mode==='sqlite'?sqliteDatabase():postgresDatabase(process.env.TEST_DATABASE_URL!);await migrate(db);const store=new Store(db),devices=new DeviceStore(store);try{const owner=(await store.ensureOwner('test',randomUUID(),'Owner')).id,device=randomUUID(),enrollment=randomUUID();await store.reserve(owner,device,enrollment);await store.redeem(owner,device,enrollment,'Home');const identity=await devices.authenticate(await devices.issue(owner,device));const results=await Promise.allSettled(Array.from({length:100},()=>devices.create(identity,{type:'command',id:randomUUID(),deviceId:device,epoch:identity.epoch,bootId:'boot',action:'eyes',args:{expression:'neutral'},deadline:Date.now()+60000})));expect(results.filter(r=>r.status==='fulfilled')).toHaveLength(16);expect(results.filter(r=>r.status==='rejected')).toHaveLength(84);}finally{await db.close();}});
