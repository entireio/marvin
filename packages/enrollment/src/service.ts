import { createPrivateKey,createPublicKey,sign,verify,createHash,randomUUID,randomBytes,createCipheriv,createDecipheriv,type KeyObject } from 'node:crypto';
import { z } from 'zod';
import { Store,hash } from '../../persistence/src/store.js';
import { DomainError,Id } from '../../contracts/src/index.js';
export const Challenge=z.object({deviceId:Id,nonce:z.string().regex(/^[a-f0-9]{64}$/),operation:z.enum(['claim','network','reconcile']),issuedAt:z.number().int().positive()}).strict();
export const EnrollmentRequest=z.object({challenge:Challenge,proof:z.string().regex(/^[A-Za-z0-9_-]+$/).max(200)}).strict();
export const Redemption=z.object({ticket:z.string().max(4096),network:z.string().min(1).max(128),proof:z.string().regex(/^[A-Za-z0-9_-]+$/).max(200)}).strict();
const Claims=z.object({iss:z.string(),aud:Id,sub:Id,jti:Id,iat:z.number().int(),exp:z.number().int(),op:z.enum(['claim','network','reconcile']),nonce:z.string(),epoch:z.number().int().positive()}).strict();
export const challengeMessage=(c:z.infer<typeof Challenge>)=>`marvin-setup-v1:${c.deviceId}:${c.nonce}:${c.operation}:${c.issuedAt}`;
export const redemptionMessage=(ticket:string,network:string)=>`marvin-redeem-v1:${hash(ticket)}:${Buffer.from(network,'utf8').toString('base64url')}`;
export const deviceFingerprint=(publicKey:string|KeyObject)=>'marvin_'+createHash('sha256').update((typeof publicKey==='string'?createPublicKey(publicKey):publicKey).export({format:'der',type:'spki'})).digest('hex').slice(0,32);
export class EnrollmentService {
 private signingKey:KeyObject;private publicKey:KeyObject;readonly keyId:string;
 constructor(readonly store:Store,private privatePem:string,private sealKey:Buffer,readonly origin:string){
  const url=new URL(origin);if(url.protocol!=='https:'||['localhost','127.0.0.1','[::1]'].includes(url.hostname)||url.pathname!=='/'||url.username||url.password||url.search||url.hash)throw new Error('Device enrollment needs a reachable HTTPS origin.');if(sealKey.length!==32)throw new Error('Enrollment receipt key must be 32 bytes.');
  this.signingKey=createPrivateKey(privatePem);if(this.signingKey.asymmetricKeyType!=='ec'||this.signingKey.asymmetricKeyDetails?.namedCurve!=='prime256v1')throw new Error('Enrollment signing uses a P-256 key.');this.publicKey=createPublicKey(this.signingKey);this.keyId=hash(this.publicKey.export({format:'pem',type:'spki'}).toString()).slice(0,16);
 }
 identity(){return {issuer:this.origin,keyId:this.keyId,algorithm:'ES256',publicKey:this.publicKey.export({format:'jwk'})};}
 /** Trusted local/factory registration only; never exposed as an unauthenticated API. */
 async registerDevice(publicPem:string){const key=createPublicKey(publicPem);if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails?.namedCurve!=='prime256v1')throw new Error('Devices require P-256 identity keys.');const id=deviceFingerprint(key);await this.store.db.query('INSERT INTO device_identities(device_id,public_key,registered_at) VALUES (?,?,?) ON CONFLICT(device_id) DO NOTHING',[id,key.export({format:'pem',type:'spki'}),Date.now()]);return id;}
 private async key(deviceId:string){const row=(await this.store.db.query<{public_key:string}>('SELECT public_key FROM device_identities WHERE device_id=?',[deviceId]))[0];if(!row)throw new DomainError('UNKNOWN_DEVICE','This device identity has not been registered with this deployment.',403);return createPublicKey(row.public_key);}
 private proof(key:KeyObject,message:string,signature:string){if(!verify('sha256',Buffer.from(message),{key,dsaEncoding:'ieee-p1363'},Buffer.from(signature,'base64url')))throw new DomainError('DEVICE_PROOF_INVALID','The device did not prove its identity.',403);}
 private encode(payload:z.infer<typeof Claims>){const head=Buffer.from(JSON.stringify({alg:'ES256',typ:'JWT',kid:this.keyId})).toString('base64url'),body=Buffer.from(JSON.stringify(payload)).toString('base64url'),data=head+'.'+body;return data+'.'+sign('sha256',Buffer.from(data),{key:this.signingKey,dsaEncoding:'ieee-p1363'}).toString('base64url');}
 private decode(ticket:string){try{const parts=ticket.split('.');if(parts.length!==3)throw new Error();const header=JSON.parse(Buffer.from(parts[0],'base64url').toString());if(header.alg!=='ES256'||header.kid!==this.keyId||header.typ!=='JWT'||!verify('sha256',Buffer.from(parts[0]+'.'+parts[1]),{key:this.publicKey,dsaEncoding:'ieee-p1363'},Buffer.from(parts[2],'base64url')))throw new Error();const claims=Claims.parse(JSON.parse(Buffer.from(parts[1],'base64url').toString()));if(claims.iss!==this.origin)throw new Error();return claims;}catch{throw new DomainError('TICKET_INVALID','The setup ticket is invalid or expired. Start setup again.',403);}}
 private seal(value:unknown){const nonce=randomBytes(12),cipher=createCipheriv('aes-256-gcm',this.sealKey,nonce);cipher.setAAD(Buffer.from('marvin-enrollment-receipt-v1'));const encrypted=Buffer.concat([cipher.update(JSON.stringify(value),'utf8'),cipher.final()]);return Buffer.concat([nonce,cipher.getAuthTag(),encrypted]).toString('base64url');}
 private unseal(value:string){const bytes=Buffer.from(value,'base64url'),decipher=createDecipheriv('aes-256-gcm',this.sealKey,bytes.subarray(0,12));decipher.setAAD(Buffer.from('marvin-enrollment-receipt-v1'));decipher.setAuthTag(bytes.subarray(12,28));return JSON.parse(Buffer.concat([decipher.update(bytes.subarray(28)),decipher.final()]).toString());}
 async ticket(ownerId:string,request:unknown){const {challenge:c,proof}=EnrollmentRequest.parse(request);if(Math.abs(Date.now()-c.issuedAt)>120000)throw new DomainError('SETUP_EXPIRED','Reopen setup on Marvin and try again.',409);this.proof(await this.key(c.deviceId),challengeMessage(c),proof);
  const id=randomUUID(),expires=Date.now()+120000;
  try{return await this.store.db.transaction(async tx=>{
   await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);await tx.query("DELETE FROM body_slots WHERE state='reserved' AND expires_at<=?",[Date.now()]);
   const slot=(await tx.query<{owner_id:string;device_id:string;epoch:number;state:string}>('SELECT * FROM body_slots WHERE owner_id=? OR device_id=?',[ownerId,c.deviceId]));
   if(c.operation==='network'&&(slot.length!==1||slot[0].owner_id!==ownerId||slot[0].device_id!==c.deviceId||slot[0].state!=='linked'))throw new DomainError('DEVICE_FORBIDDEN','Only the linked owner can change this device’s network.',403);
   if(c.operation==='claim'&&slot.length)throw new DomainError('ALREADY_LINKED','An account and a device can each have only one active link.',409);
   await tx.query('INSERT INTO device_epochs(device_id,epoch) VALUES (?,1) ON CONFLICT(device_id) DO NOTHING',[c.deviceId]);let epoch=Number((await tx.query<{epoch:number}>('SELECT epoch FROM device_epochs WHERE device_id=?',[c.deviceId]))[0].epoch);
   let ticketOwnerId=ownerId;
   if(c.operation==='reconcile'){
    if(slot.length)throw new DomainError('ACTIVE_BINDING','This Marvin still has an active account link.',409);
    const recorded=(await tx.query<{former_owner_id:string;revoked_epoch:number;acknowledged_at:number|null}>('SELECT former_owner_id,revoked_epoch,acknowledged_at FROM device_revocations WHERE device_id=?',[c.deviceId]))[0];
    if(recorded?.acknowledged_at)throw new DomainError('RECOVERY_NOT_AUTHORIZED','This ownership cleanup has already been completed.',403);
    let revocation=recorded?{former_owner_id:recorded.former_owner_id,revoked_epoch:Number(recorded.revoked_epoch)}:undefined;
    // Migration compatibility: completed claim tickets prove the former binding
    // for devices unlinked before revocation records were introduced.
    if(!revocation){const previous=(await tx.query<{owner_id:string;epoch:number}>("SELECT owner_id,epoch FROM enrollment_tickets WHERE device_id=? AND operation='claim' AND receipt IS NOT NULL ORDER BY expires_at DESC LIMIT 1",[c.deviceId]))[0];if(previous&&Number(previous.epoch)+1===epoch){await tx.query('INSERT INTO device_revocations(device_id,former_owner_id,revoked_epoch,replacement_epoch,revoked_at,acknowledged_at) VALUES (?,?,?,?,?,NULL) ON CONFLICT(device_id) DO NOTHING',[c.deviceId,previous.owner_id,Number(previous.epoch),epoch,Date.now()]);revocation={former_owner_id:previous.owner_id,revoked_epoch:Number(previous.epoch)};}}
    if(!revocation)throw new DomainError('RECOVERY_NOT_AUTHORIZED','This account cannot clear the ownership stored on this Marvin.',403);
    // The authenticated setup account proves physical access to this exact
    // device. The ticket must still name the former owner so firmware can only
    // clear the already-revoked local binding, never replace an active owner.
    ticketOwnerId=revocation.former_owner_id;
    epoch=Number(revocation.revoked_epoch);
   }
   const ticket=this.encode({iss:this.origin,aud:c.deviceId,sub:ticketOwnerId,jti:id,iat:Math.floor(Date.now()/1000),exp:Math.floor(expires/1000),op:c.operation,nonce:c.nonce,epoch});
   await tx.query('INSERT INTO enrollment_tickets(id,owner_id,device_id,nonce,operation,epoch,ticket_hash,expires_at) VALUES (?,?,?,?,?,?,?,?)',[id,ownerId,c.deviceId,c.nonce,c.operation,epoch,hash(ticket),expires]);
   if(c.operation==='claim')await tx.query("INSERT INTO body_slots(owner_id,device_id,enrollment_id,state,expires_at,epoch,simulated) VALUES (?,?,?,'reserved',?,?,0)",[ownerId,c.deviceId,id,expires,epoch]);
   return {ticket,enrollmentId:id,expiresAt:expires,backend:this.identity()};
  });}catch(e){if(e instanceof DomainError)throw e;throw new DomainError('CLAIM_CONFLICT','Setup is already active or its challenge was already used. Start a new setup attempt.',409);}
 }
 async redeem(request:unknown){const {ticket,network,proof}=Redemption.parse(request),claims=this.decode(ticket);this.proof(await this.key(claims.aud),redemptionMessage(ticket,network),proof);
  return this.store.db.transaction(async tx=>{
   await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[claims.sub]);
   await tx.query('UPDATE body_slots SET epoch=epoch WHERE owner_id=? AND device_id=?',[claims.sub,claims.aud]);
   await tx.query('UPDATE enrollment_tickets SET expires_at=expires_at WHERE id=?',[claims.jti]);const row=(await tx.query<{ticket_hash:string;receipt:string|null;network:string|null;expires_at:number}>('SELECT * FROM enrollment_tickets WHERE id=? AND owner_id=? AND device_id=?',[claims.jti,claims.sub,claims.aud]))[0];
   const slot=(await tx.query<{state:string;enrollment_id:string}>("SELECT state,enrollment_id FROM body_slots WHERE owner_id=? AND device_id=? AND epoch=?",[claims.sub,claims.aud,claims.epoch]))[0];if(!row||row.ticket_hash!==hash(ticket)||!slot)throw new DomainError('ENROLLMENT_REVOKED','This setup ticket is no longer authorized.',403);
   if(row.receipt){
    if(row.network!==network)throw new DomainError('TRANSACTION_CONFLICT','A completed setup cannot be reused for a different network.',409);
    // A device may lose power after the server commits but before it persists the reply.
    // Recover only the current completed transaction, never a fresh expired claim.
    if(slot.state!=='linked'||slot.enrollment_id!==claims.jti)throw new DomainError('ENROLLMENT_REVOKED','A newer setup has replaced this transaction.',403);
    const result=this.unseal(row.receipt);
    if(result.credential){const active=await tx.query('SELECT hash FROM device_credentials WHERE hash=? AND device_id=? AND owner_id=? AND epoch=? AND expires_at>?',[hash(result.credential),claims.aud,claims.sub,claims.epoch,Date.now()]);if(!active.length)throw new DomainError('ENROLLMENT_REVOKED','The setup credential is no longer valid.',403);}
    return result;
   }
   if(row.expires_at<=Date.now()||claims.exp*1000<=Date.now())throw new DomainError('ENROLLMENT_REVOKED','This setup ticket has expired. Start setup again.',403);
   if(claims.op==='claim'&&(slot.state!=='reserved'||slot.enrollment_id!==claims.jti))throw new DomainError('ENROLLMENT_REVOKED','The device reservation changed.',403);
   if(claims.op==='network'&&slot.state!=='linked')throw new DomainError('DEVICE_FORBIDDEN','The device is no longer linked.',403);
   const result:{linked:boolean;deviceId:string;epoch:number;credential?:string;credentialExpiresAt?:number}={linked:true,deviceId:claims.aud,epoch:claims.epoch};
   if(claims.op==='claim'){const token=randomBytes(32).toString('base64url'),expiry=Date.now()+30*86400000;await tx.query('DELETE FROM device_credentials WHERE device_id=?',[claims.aud]);await tx.query('INSERT INTO device_credentials(hash,device_id,owner_id,epoch,expires_at) VALUES (?,?,?,?,?)',[hash(token),claims.aud,claims.sub,claims.epoch,expiry]);result.credential=token;result.credentialExpiresAt=expiry;}
   await tx.query("UPDATE body_slots SET state='linked',network=?,enrollment_id=? WHERE owner_id=? AND device_id=? AND epoch=?",[network,claims.jti,claims.sub,claims.aud,claims.epoch]);await tx.query('UPDATE enrollment_tickets SET receipt=?,network=? WHERE id=?',[this.seal(result),network,claims.jti]);return result;
  });
 }
 async cancel(ownerId:string,id:string){await this.store.db.transaction(async tx=>{await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);await tx.query('UPDATE enrollment_tickets SET expires_at=? WHERE id=? AND owner_id=? AND receipt IS NULL',[0,id,ownerId]);await tx.query("DELETE FROM body_slots WHERE enrollment_id=? AND owner_id=? AND state='reserved'",[id,ownerId]);});}
}
