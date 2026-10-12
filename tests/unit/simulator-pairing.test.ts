import {afterEach,expect,it} from 'vitest';
import {createApp} from '../../apps/server/src/app.js';
import {config} from '../../apps/server/src/config.js';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {DeviceSimulator} from '../../packages/device/src/simulator.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';
import {deleteAccount} from '../../packages/persistence/src/privacy.js';

const services:Awaited<ReturnType<typeof createApp>>[]=[],simulators:DeviceSimulator[]=[];
afterEach(async()=>{for(const simulator of simulators.splice(0))simulator.close();for(const service of services.splice(0))await service.app.close();});
async function rig(enabled='true'){
 const service=await createApp(config({NODE_ENV:'test',SIMULATOR_PAIRING_ENABLED:enabled}),{database:sqliteDatabase()});services.push(service);
 const owner=await service.store.ensureOwner('test',String(services.length),'Owner'),session=await service.store.createSession(owner.id);
 const headers={origin:'http://127.0.0.1:5173',cookie:'marvin_session='+session.token,'x-csrf-token':session.csrf};
 const issue=()=>service.app.inject({method:'POST',url:'/api/robot/simulator/pairing',headers,payload:{}});
 const redeem=(code:string)=>service.app.inject({method:'POST',url:'/api/device/simulator/pair',payload:{code}});
 return {service,owner,headers,issue,redeem};
}
it('requires an authenticated same-origin pairing request and rejects browser redemption',async()=>{
 const r=await rig();
 expect((await r.service.app.inject({method:'POST',url:'/api/robot/simulator/pairing',headers:{origin:r.headers.origin},payload:{}})).statusCode).toBe(401);
 expect((await r.service.app.inject({method:'POST',url:'/api/robot/simulator/pairing',headers:{...r.headers,'x-csrf-token':'bad'},payload:{}})).statusCode).toBe(403);
 const issued=(await r.issue()).json();expect(issued.code).toHaveLength(43);
 expect((await r.service.app.inject({method:'POST',url:'/api/device/simulator/pair',headers:r.headers,payload:{code:issued.code}})).statusCode).toBe(403);
 const paired=await r.redeem(issued.code);expect(paired.statusCode).toBe(200);
 expect(await r.service.devices.persistence.authenticate(paired.json().credential)).toMatchObject({ownerId:r.owner.id,deviceId:paired.json().deviceId});
 expect(await r.service.store.body(r.owner.id)).toMatchObject({kind:'simulator',simulated:1,state:'linked'});
 expect((await r.redeem(issued.code)).statusCode).toBe(401);
});
it('expires, replaces and cancels one-time codes and admits one concurrent redemption',async()=>{
 const r=await rig(),old=(await r.issue()).json().code,active=(await r.issue()).json().code;
 expect((await r.redeem(old)).statusCode).toBe(401);
 await r.service.store.db.query('UPDATE simulator_pairings SET expires_at=0');expect((await r.redeem(active)).statusCode).toBe(401);
 const cancelled=(await r.issue()).json().code;
 await r.service.app.inject({method:'DELETE',url:'/api/robot/simulator/pairing',headers:r.headers});expect((await r.redeem(cancelled)).statusCode).toBe(401);
 const code=(await r.issue()).json().code,results=await Promise.all([r.redeem(code),r.redeem(code)]);
 expect(results.map(result=>result.statusCode).sort()).toEqual([200,401]);
});
it('never replaces a physical binding even when it appears after code issuance',async()=>{
 const r=await rig(),code=(await r.issue()).json().code;
 await r.service.store.reserve(r.owner.id,'physical-pet','physical-enrollment');await r.service.store.redeem(r.owner.id,'physical-pet','physical-enrollment','Home');
 expect((await r.issue()).statusCode).toBe(409);expect((await r.redeem(code)).statusCode).toBe(409);
 expect(await r.service.store.body(r.owner.id)).toMatchObject({device_id:'physical-pet',kind:'physical',network:'Home'});
});
it('connects through the ordinary gateway and remote; unlink revokes its credential',async()=>{
 const r=await rig(),code=(await r.issue()).json().code,paired=(await r.redeem(code)).json();
 await r.service.app.listen({host:'127.0.0.1',port:0});const address=r.service.app.server.address();if(!address||typeof address==='string')throw new Error('Missing port');
 const simulator=new DeviceSimulator(paired.deviceId,undefined,['remote','head','eyes']);simulators.push(simulator);
 await simulator.connect(`ws://127.0.0.1:${address.port}/api/device/socket`,paired.credential,13);
 expect(r.service.devices.presence(r.owner.id)).toMatchObject({deviceId:paired.deviceId,status:'online'});
 expect((await r.service.devices.dispatchRemote(r.owner.id,'remote',{throttle:.5,turn:0,headYaw:0,headPitch:0,autonomousHead:true,sequence:1},'remote-command',1000)).accepted).toBe(true);
 const unlinked=await r.service.app.inject({method:'DELETE',url:'/api/robot',headers:r.headers});expect(unlinked.statusCode).toBe(200);
 await expect(r.service.devices.persistence.authenticate(paired.credential)).rejects.toMatchObject({code:'DEVICE_UNAUTHENTICATED'});
 expect(await r.service.store.body(r.owner.id)).toBeNull();
});
it('honors the deployment flag and removes outstanding codes on account deletion',async()=>{
 const disabled=await rig('false');expect((await disabled.issue()).statusCode).toBe(403);expect((await disabled.redeem('a'.repeat(43))).statusCode).toBe(403);
 const r=await rig();await r.issue();await deleteAccount(r.service.store,r.owner.id);
 expect(await r.service.store.db.query('SELECT * FROM simulator_pairings')).toEqual([]);
});

it('upgrades legacy preview and physical identities without reclassifying hardware',async()=>{
 const db=sqliteDatabase();try {
  await migrate(db,10);const store=new Store(db);
  for(const simulated of [0,1]){
   const owner=await store.ensureOwner('test',String(simulated),'Owner');
   await db.query("INSERT INTO body_slots(owner_id,device_id,enrollment_id,state,expires_at,simulated) VALUES (?,?,?,'linked',?,?)",[owner.id,'pet-'+simulated,'enrollment-'+simulated,Date.now()+10000,simulated]);
  }
  await migrate(db);expect((await db.query<{kind:string}>('SELECT kind FROM body_slots ORDER BY simulated')).map(row=>row.kind)).toEqual(['physical','preview']);
 } finally {await db.close();}
});
