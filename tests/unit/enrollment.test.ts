import {beforeEach,afterEach,describe,it,expect,vi} from 'vitest';
import {generateKeyPairSync,sign,randomBytes,randomUUID, type KeyObject} from 'node:crypto';
import {sqliteDatabase,postgresDatabase,type Database} from '../../packages/persistence/src/database.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';
import {DeviceStore} from '../../packages/device/src/store.js';
import {EnrollmentService,challengeMessage,redemptionMessage} from '../../packages/enrollment/src/service.js';
for(const mode of ['sqlite',...(process.env.TEST_DATABASE_URL?['postgres']:[])])describe(`enrollment conformance: ${mode}`,()=>{
 let db:Database,store:Store,service:EnrollmentService,owner:string,other:string,device:string,key:KeyObject;
 beforeEach(async()=>{db=mode==='sqlite'?sqliteDatabase():postgresDatabase(process.env.TEST_DATABASE_URL!);await migrate(db);store=new Store(db);owner=(await store.ensureOwner('test',randomUUID(),'Owner')).id;other=(await store.ensureOwner('test',randomUUID(),'Other')).id;const signing=generateKeyPairSync('ec',{namedCurve:'prime256v1'});service=new EnrollmentService(store,signing.privateKey.export({format:'pem',type:'pkcs8'}).toString(),randomBytes(32),'https://marvin.example');const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'});key=pair.privateKey;device=await service.registerDevice(pair.publicKey.export({format:'pem',type:'spki'}).toString());});
 afterEach(async()=>{vi.restoreAllMocks();await db.close();});
 function proof(message:string){return sign('sha256',Buffer.from(message),{key,dsaEncoding:'ieee-p1363'}).toString('base64url');}
 function request(operation:'claim'|'network'|'reconcile'='claim'){const challenge={deviceId:device,nonce:randomBytes(32).toString('hex'),operation,issuedAt:Date.now()};return {challenge,proof:proof(challengeMessage(challenge))};}
 const redeem=(ticket:string,network='Home')=>service.redeem({ticket,network,proof:proof(redemptionMessage(ticket,network))});
 it('claim response loss retries with the same credential; network changes preserve identity and history',async()=>{const c=await store.createConversation(owner);const ticket=await service.ticket(owner,request());expect(await store.body(owner)).toBeNull();const first=await redeem(ticket.ticket);expect(first.credential).toBeTruthy();expect(await redeem(ticket.ticket)).toEqual(first);expect((await new DeviceStore(store).authenticate(first.credential)).ownerId).toBe(owner);const change=await service.ticket(owner,request('network'));expect((await redeem(change.ticket,'Friend')).credential).toBeUndefined();expect((await store.body(owner))?.network).toBe('Friend');expect((await new DeviceStore(store).authenticate(first.credential)).deviceId).toBe(device);expect((await store.getConversation(owner,c.id)).id).toBe(c.id);});
 it('concurrent competing owners cannot reserve a device twice',async()=>{const results=await Promise.allSettled([service.ticket(owner,request()),service.ticket(other,request())]);expect(results.filter(r=>r.status==='fulfilled')).toHaveLength(1);});
 it('an account cannot claim a second device',async()=>{await redeem((await service.ticket(owner,request())).ticket);const next=generateKeyPairSync('ec',{namedCurve:'prime256v1'});device=await service.registerDevice(next.publicKey.export({format:'pem',type:'spki'}).toString());key=next.privateKey;await expect(service.ticket(owner,request())).rejects.toMatchObject({code:'ALREADY_LINKED'});});
 it('forged device proof, stale challenge and non-owner network change are refused',async()=>{const bad=request();bad.proof=Buffer.alloc(64).toString('base64url');await expect(service.ticket(owner,bad)).rejects.toMatchObject({code:'DEVICE_PROOF_INVALID'});const stale=request();stale.challenge.issuedAt-=180000;stale.proof=proof(challengeMessage(stale.challenge));await expect(service.ticket(owner,stale)).rejects.toMatchObject({code:'SETUP_EXPIRED'});await redeem((await service.ticket(owner,request())).ticket);await expect(service.ticket(other,request('network'))).rejects.toMatchObject({code:'DEVICE_FORBIDDEN'});});
 it('ticket, network and identity substitution fail cryptographic verification',async()=>{const {ticket}=await service.ticket(owner,request());await expect(redeem(ticket.slice(0,-5)+'AAAAA')).rejects.toMatchObject({code:'TICKET_INVALID'});await expect(service.redeem({ticket,network:'Other',proof:proof(redemptionMessage(ticket,'Home'))})).rejects.toMatchObject({code:'DEVICE_PROOF_INVALID'});});
 it('unlink revokes both device credentials and retries of a previously redeemed ticket',async()=>{const {ticket}=await service.ticket(owner,request());const result=await redeem(ticket);await store.unlink(owner);await expect(new DeviceStore(store).authenticate(result.credential)).rejects.toThrow();await expect(redeem(ticket)).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});});
 it('lets the next owner clear the exact revoked local binding once',async()=>{const {ticket}=await service.ticket(owner,request());await redeem(ticket);await store.unlink(owner);expect(await store.acknowledgeRevocation(other,device)).toBe(false);const reconciliation=await service.ticket(other,request('reconcile'));const claims=JSON.parse(Buffer.from(reconciliation.ticket.split('.')[1],'base64url').toString());expect(claims).toMatchObject({aud:device,sub:owner,op:'reconcile',epoch:1});expect(await store.acknowledgeRevocation(other,device)).toBe(true);await expect(service.ticket(owner,request('reconcile'))).rejects.toMatchObject({code:'RECOVERY_NOT_AUTHORIZED'});});
 it('recovers a pre-migration unlink for the next owner from its completed claim receipt',async()=>{const {ticket}=await service.ticket(owner,request());await redeem(ticket);await store.unlink(owner);await db.query('DELETE FROM device_revocations WHERE device_id=?',[device]);const reconciliation=await service.ticket(other,request('reconcile'));const claims=JSON.parse(Buffer.from(reconciliation.ticket.split('.')[1],'base64url').toString());expect(claims.sub).toBe(owner);});
 it('cancelled claims release reservations but their nonce cannot be reused',async()=>{const req=request(),issued=await service.ticket(owner,req);await service.cancel(owner,issued.enrollmentId);await expect(redeem(issued.ticket)).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});await expect(service.ticket(owner,req)).rejects.toMatchObject({code:'CLAIM_CONFLICT'});await expect(service.ticket(owner,request())).resolves.toBeDefined();});
 it('idempotent receipt cannot apply a different network and raw credentials are absent from database rows',async()=>{const {ticket}=await service.ticket(owner,request()),r=await redeem(ticket);await expect(redeem(ticket,'Different')).rejects.toMatchObject({code:'TRANSACTION_CONFLICT'});const rows=await db.query('SELECT receipt FROM enrollment_tickets WHERE owner_id=?',[owner]);expect(JSON.stringify(rows)).not.toContain(r.credential);});
 it('recovers a completed reply after ticket expiry but refuses an unused expired ticket',async()=>{
  const issued=await service.ticket(owner,request()),first=await redeem(issued.ticket);
  const pending=await service.ticket(owner,request('network'));
  const now=Date.now();vi.spyOn(Date,'now').mockReturnValue(now+180000);
  expect(await redeem(issued.ticket)).toEqual(first);
  await expect(redeem(pending.ticket,'Friend')).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});
 });
 it('a newer completed network change supersedes old receipts, even for the same SSID',async()=>{
  const initial=await service.ticket(owner,request());await redeem(initial.ticket);
  const change=await service.ticket(owner,request('network'));await redeem(change.ticket);
  await expect(redeem(initial.ticket)).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});
  const now=Date.now();vi.spyOn(Date,'now').mockReturnValue(now+180000);
  expect((await redeem(change.ticket)).linked).toBe(true);
 });
 it('cannot recover a credential after its independent expiry or revocation',async()=>{
  const issued=await service.ticket(owner,request());await redeem(issued.ticket);
  const now=Date.now();vi.spyOn(Date,'now').mockReturnValue(now+31*86400000);
  await expect(redeem(issued.ticket)).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});
 });

 it('simultaneous redemption and unlink finish with no usable credential or recovered receipt',async()=>{
  for(let n=0;n<20;n++){
   const issued=await service.ticket(owner,request());const results=await Promise.allSettled([redeem(issued.ticket),store.unlink(owner)]);
   expect(results[1].status).toBe('fulfilled');expect(await store.body(owner)).toBeNull();
   if(results[0].status==='fulfilled')await expect(new DeviceStore(store).authenticate(results[0].value.credential)).rejects.toThrow();
   await expect(redeem(issued.ticket)).rejects.toMatchObject({code:'ENROLLMENT_REVOKED'});
  }
 });

});
