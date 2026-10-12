import {randomBytes,randomUUID} from 'node:crypto';
import {DomainError} from '../../contracts/src/index.js';
import {Store,hash} from '../../persistence/src/store.js';

/** Account-authorized software enrollment. Never replaces a hardware binding. */
export class SimulatorPairing {
 constructor(readonly store:Store){}
 async cancel(ownerId:string){await this.store.db.query('DELETE FROM simulator_pairings WHERE owner_id=?',[ownerId]);}
 async create(ownerId:string){
  const code=randomBytes(32).toString('base64url'),expiresAt=Date.now()+5*60_000;
  await this.store.db.transaction(async tx=>{
   await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);
   await tx.query("DELETE FROM body_slots WHERE state='reserved' AND expires_at<=?",[Date.now()]);
   if((await tx.query('SELECT device_id FROM body_slots WHERE owner_id=?',[ownerId])).length)throw new DomainError('ALREADY_LINKED','Unlink your current Desktop Pet before pairing a simulator.',409);
   await tx.query('DELETE FROM simulator_pairings WHERE owner_id=? OR expires_at<=?',[ownerId,Date.now()]);
   await tx.query('INSERT INTO simulator_pairings(hash,owner_id,expires_at) VALUES (?,?,?)',[hash(code),ownerId,expiresAt]);
  });
  return {code,expiresAt};
 }
 async redeem(code:string){
  // Read only to acquire the same owner lock as physical enrollment, then
  // atomically consume. Losing a race never consumes a recoverable code.
  return this.store.db.transaction(async tx=>{
   const candidate=(await tx.query<{owner_id:string}>('SELECT owner_id FROM simulator_pairings WHERE hash=? AND expires_at>?',[hash(code),Date.now()]))[0];
   if(!candidate)throw new DomainError('PAIRING_INVALID','Pairing expired or was already used. Create a new simulator code in Marvin.',401);
   await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[candidate.owner_id]);
   const pairing=(await tx.query<{owner_id:string}>('DELETE FROM simulator_pairings WHERE hash=? AND expires_at>? RETURNING owner_id',[hash(code),Date.now()]))[0];
   if(!pairing)throw new DomainError('PAIRING_INVALID','Pairing expired or was already used. Create a new simulator code in Marvin.',401);
   await tx.query("DELETE FROM body_slots WHERE state='reserved' AND expires_at<=?",[Date.now()]);
   if((await tx.query('SELECT device_id FROM body_slots WHERE owner_id=?',[pairing.owner_id])).length)throw new DomainError('ALREADY_LINKED','This account already has a Desktop Pet. Unlink it before pairing a simulator.',409);
   const deviceId='simulator_'+randomBytes(16).toString('hex'),credential=randomBytes(32).toString('base64url'),credentialExpiresAt=Date.now()+30*86400_000;
   await tx.query('INSERT INTO device_epochs(device_id,epoch) VALUES (?,1)',[deviceId]);
   await tx.query("INSERT INTO body_slots(owner_id,device_id,enrollment_id,state,expires_at,epoch,simulated,kind,network) VALUES (?,?,?,'linked',?,1,1,'simulator','Simulator app')",[pairing.owner_id,deviceId,randomUUID(),credentialExpiresAt]);
   await tx.query('INSERT INTO device_credentials(hash,device_id,owner_id,epoch,expires_at) VALUES (?,?,?,1,?)',[hash(credential),deviceId,pairing.owner_id,credentialExpiresAt]);
   return {deviceId,credential,credentialExpiresAt};
  });
 }
}
