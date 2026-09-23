import {describe,it,expect} from 'vitest';
import {createPublicKey,generateKeyPairSync,verify} from 'node:crypto';
import {randomUUID} from 'node:crypto';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';
import {RECOVERY_ISSUER,recoveryPublicKey,signRecoveryTicket} from '../../packages/enrollment/src/recovery.js';

describe('fleet return recovery',()=>{
 it('issues a device-bound short-lived ticket with a separate P-256 key',()=>{
  const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),privatePem=pair.privateKey.export({format:'pem',type:'pkcs8'}).toString(),now=Date.now(),deviceId='marvin_'+'a'.repeat(32),ownerId=randomUUID();
  const ticket=signRecoveryTicket(privatePem,{challenge:{deviceId,nonce:'b'.repeat(64),operation:'recover',issuedAt:now},ownerId,epoch:3,operatorId:'operator@example',caseId:'return-42'},now),parts=ticket.split('.');
  expect(parts).toHaveLength(3);const claims=JSON.parse(Buffer.from(parts[1]!,'base64url').toString());expect(claims).toMatchObject({iss:RECOVERY_ISSUER,aud:deviceId,sub:ownerId,epoch:3,op:'recover',operator:'operator@example',case:'return-42'});expect(claims.exp-claims.iat).toBe(120);
  expect(verify('sha256',Buffer.from(parts[0]+'.'+parts[1]),{key:createPublicKey(recoveryPublicKey(privatePem)),dsaEncoding:'ieee-p1363'},Buffer.from(parts[2]!,'base64url'))).toBe(true);
 });
 it('operator unlink revokes a binding without touching another Pet and records the action',async()=>{
  const db=sqliteDatabase();await migrate(db);const store=new Store(db),owner=(await store.ensureOwner('test','returned','Owner')).id,other=(await store.ensureOwner('test','active','Other')).id,a='marvin_'+'a'.repeat(32),b='marvin_'+'b'.repeat(32);
  try{const enrollmentA=randomUUID(),enrollmentB=randomUUID();await store.reserve(owner,a,enrollmentA);await store.redeem(owner,a,enrollmentA,'Home');await store.reserve(other,b,enrollmentB);await store.redeem(other,b,enrollmentB,'Office');
   const result=await store.operatorUnlinkDevice(a,'stefano','returned hardware','cloud');expect(result).toMatchObject({deviceId:a,linked:true,formerOwnerId:owner,formerEpoch:1,deviceErasureConfirmed:false});expect(await store.body(owner)).toBeNull();expect((await store.body(other))?.device_id).toBe(b);expect(await db.query('SELECT device_id FROM device_revocations WHERE device_id=?',[a])).toHaveLength(1);expect(await db.query('SELECT id FROM operator_device_recoveries WHERE device_id=? AND operator_id=?',[a,'stefano'])).toHaveLength(1);
  }finally{await db.close();}
 });
});
