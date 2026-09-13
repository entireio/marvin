import { randomBytes } from 'node:crypto';
import { Store,hash } from '../../persistence/src/store.js';
import { DomainError } from '../../contracts/src/index.js';
import type { DeviceCommand } from '../../contracts/src/device.js';
export type DeviceIdentity={deviceId:string;ownerId:string;epoch:number};
type Row={id:string;device_id:string;owner_id:string;epoch:number;boot_id:string;action:DeviceCommand['action'];args_json:string;deadline:number;state:string};
export class DeviceStore {
 constructor(readonly store:Store){}
 /** Administrative/internal bootstrap only. Production issuance belongs to verified enrollment. */
 async issue(ownerId:string,deviceId:string,ttlMs=30*24*60*60*1000){const token=randomBytes(32).toString('base64url');await this.store.db.transaction(async tx=>{
  await tx.query('UPDATE body_slots SET epoch=epoch WHERE owner_id=? AND device_id=?',[ownerId,deviceId]);
  const slot=(await tx.query<{epoch:number}>("SELECT epoch FROM body_slots WHERE owner_id=? AND device_id=? AND state='linked'",[ownerId,deviceId]))[0];if(!slot)throw new DomainError('DEVICE_FORBIDDEN','Link this device before issuing its credential.',403);
  await tx.query('DELETE FROM device_credentials WHERE device_id=?',[deviceId]);await tx.query('INSERT INTO device_credentials(hash,device_id,owner_id,epoch,expires_at) VALUES (?,?,?,?,?)',[hash(token),deviceId,ownerId,slot.epoch,Date.now()+ttlMs]);
 });return token;}
 async authenticate(token:string):Promise<DeviceIdentity>{if(!/^[A-Za-z0-9_-]{43}$/.test(token))throw new DomainError('DEVICE_UNAUTHENTICATED','Device authorization expired or was revoked.',401);
  const r=(await this.store.db.query<{device_id:string;owner_id:string;epoch:number}>("SELECT c.device_id,c.owner_id,c.epoch FROM device_credentials c JOIN body_slots b ON b.device_id=c.device_id AND b.owner_id=c.owner_id AND b.epoch=c.epoch JOIN device_epochs e ON e.device_id=c.device_id AND e.epoch=c.epoch WHERE c.hash=? AND c.expires_at>? AND b.state='linked'",[hash(token),Date.now()]))[0];if(!r)throw new DomainError('DEVICE_UNAUTHENTICATED','Device authorization expired or was revoked.',401);return {deviceId:r.device_id,ownerId:r.owner_id,epoch:r.epoch};
 }
 async create(identity:DeviceIdentity,command:DeviceCommand){return this.store.db.transaction(async tx=>{
  await tx.query('UPDATE body_slots SET epoch=epoch WHERE owner_id=? AND device_id=?',[identity.ownerId,identity.deviceId]);
  const existing=(await tx.query<Row>('SELECT * FROM device_commands WHERE id=?',[command.id]))[0];if(existing){if(existing.owner_id!==identity.ownerId||existing.device_id!==identity.deviceId||existing.epoch!==identity.epoch||existing.boot_id!==command.bootId||existing.action!==command.action||existing.args_json!==JSON.stringify(command.args))throw new DomainError('COMMAND_ID_CONFLICT','This command identifier belongs to another action.',409);return {created:false,state:existing.state};}
  const slot=(await tx.query("SELECT device_id FROM body_slots WHERE device_id=? AND owner_id=? AND epoch=? AND state='linked'",[identity.deviceId,identity.ownerId,identity.epoch]))[0];if(!slot)throw new DomainError('DEVICE_REVOKED','Device access was revoked.',403);
  const n=(await tx.query<{n:number}>("SELECT COUNT(*) AS n FROM device_commands WHERE device_id=? AND state IN ('sent','accepted') AND deadline>?",[identity.deviceId,Date.now()]))[0].n;if(Number(n)>=16)throw new DomainError('DEVICE_BUSY','Marvin has too many pending actions.',429);
  await tx.query('INSERT INTO device_commands(id,device_id,owner_id,epoch,boot_id,action,args_json,deadline,state,created_at) VALUES (?,?,?,?,?,?,?,?,?,?)',[command.id,identity.deviceId,identity.ownerId,identity.epoch,command.bootId,command.action,JSON.stringify(command.args),command.deadline,'sent',Date.now()]);return {created:true,state:'sent'};
 });}
 async acknowledge(identity:DeviceIdentity,bootId:string,id:string,state:string){
  const valid=state==='accepted'?['sent']:['sent','accepted'];
  await this.store.db.query(`UPDATE device_commands SET state=? WHERE id=? AND device_id=? AND owner_id=? AND epoch=? AND boot_id=? AND state IN (${valid.map(()=>'?').join(',')}) AND deadline>?`,[state,id,identity.deviceId,identity.ownerId,identity.epoch,bootId,...valid,Date.now()]);
 }
 async cancel(identity:DeviceIdentity,id:string){return (await this.store.db.query("UPDATE device_commands SET state='cancelled' WHERE id=? AND device_id=? AND owner_id=? AND epoch=? AND state IN ('sent','accepted') RETURNING id",[id,identity.deviceId,identity.ownerId,identity.epoch])).length>0;}
 async reconnect(identity:DeviceIdentity,bootId:string){await this.store.db.query("UPDATE device_commands SET state='unknown' WHERE device_id=? AND state IN ('sent','accepted') AND (boot_id<>? OR epoch<>?)",[identity.deviceId,bootId,identity.epoch]);await this.expire();return (await this.store.db.query<Row>("SELECT * FROM device_commands WHERE device_id=? AND owner_id=? AND epoch=? AND boot_id=? AND state IN ('sent','accepted') AND deadline>? ORDER BY created_at LIMIT 16",[identity.deviceId,identity.ownerId,identity.epoch,bootId,Date.now()])).map(r=>({type:'command' as const,id:r.id,deviceId:r.device_id,epoch:r.epoch,bootId:r.boot_id,action:r.action,args:JSON.parse(r.args_json),deadline:Number(r.deadline)}));}
 async expire(){await this.store.db.query("UPDATE device_commands SET state='expired' WHERE state IN ('sent','accepted') AND deadline<=?",[Date.now()]);}
 async state(ownerId:string,id:string){const r=(await this.store.db.query<Row>('SELECT * FROM device_commands WHERE id=? AND owner_id=?',[id,ownerId]))[0];if(!r)throw new DomainError('NOT_FOUND','Command not found.',404);return {id:r.id,state:r.state,deadline:Number(r.deadline)};}
}
