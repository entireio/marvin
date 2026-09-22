import { randomUUID, createHash, randomBytes } from 'node:crypto';
import { DomainError, type Conversation, type Turn, type InteractionContext, type AgentEvent, type RepoCard } from '../../contracts/src/index.js';
import type { Database, Sql } from './database.js';
export const hash = (value: string) => createHash('sha256').update(value).digest('hex');
export type Owner = { id: string; name: string; entireState: InteractionContext['entireState']; activeConversation: string | null; petConversation: string | null };
type Row = Record<string, any>;
const conversation = (r: Row): Conversation => ({ id:r.id,title:r.title,repositoryId:r.repository_id,updatedAt:Number(r.updated_at),summary:r.summary,summaryThrough:r.summary_through });
const turn = (r: Row): Turn => ({ id:r.id,conversationId:r.conversation_id,userText:r.user_text,assistantText:r.assistant_text,status:r.status,surface:r.surface,routeId:r.route_id,createdAt:Number(r.created_at),ordinal:r.ordinal,lastSeq:Number(r.last_seq??0),cards:JSON.parse(r.cards) });
const owner = (r: Row): Owner => ({ id:r.id,name:r.name,entireState:r.entire_state,activeConversation:r.active_conversation,petConversation:r.pet_conversation??null });
export class Store {
 constructor(public db: Database) {}
 async ensureOwner(issuer: string, subject: string, name: string): Promise<Owner> {
  await this.db.query('INSERT INTO owners(id,issuer,subject,name,created_at) VALUES (?,?,?,?,?) ON CONFLICT(issuer,subject) DO NOTHING',[randomUUID(),issuer,subject,name,Date.now()]);
  return owner((await this.db.query<Row>('SELECT * FROM owners WHERE issuer=? AND subject=?',[issuer,subject]))[0]);
 }
 async owner(id: string) { const r=await this.db.query<Row>('SELECT * FROM owners WHERE id=?',[id]); if(!r[0]) throw new DomainError('NOT_FOUND','Account not found',404); return owner(r[0]); }
 async createSession(ownerId: string, lifetime=8*60*60*1000) {
  const token=randomBytes(32).toString('base64url'), csrf=randomBytes(32).toString('base64url'), now=Date.now();
  await this.db.query('INSERT INTO sessions(hash,owner_id,csrf,expires_at,authenticated_at) VALUES (?,?,?,?,?)',[hash(token),ownerId,csrf,now+lifetime,now]);
  return { token, csrf };
 }
 async session(token?: string) { if(!token) return null; const r=(await this.db.query<Row>('SELECT * FROM sessions WHERE hash=? AND expires_at>?',[hash(token),Date.now()]))[0]; return r ? {ownerId:r.owner_id as string,csrf:r.csrf as string,authenticatedAt:Number(r.authenticated_at)} : null; }
 async logout(token: string) { await this.db.query('DELETE FROM sessions WHERE hash=?',[hash(token)]); }
 async listConversations(ownerId: string) { const recent=(await this.db.query<Row>('SELECT * FROM conversations WHERE owner_id=? ORDER BY updated_at DESC LIMIT 100',[ownerId])).map(conversation),linked=(await this.owner(ownerId)).petConversation;return linked&&!recent.some(c=>c.id===linked)?[await this.getConversation(ownerId,linked),...recent]:recent; }
 async createConversation(ownerId: string) {
  const id=randomUUID(), now=Date.now(); await this.db.transaction(async tx=>{
   await tx.query('INSERT INTO conversations(id,owner_id,title,updated_at) VALUES (?,?,?,?)',[id,ownerId,'New conversation',now]);
   await tx.query('UPDATE owners SET active_conversation=? WHERE id=?',[id,ownerId]);
  }); return this.getConversation(ownerId,id);
 }
 async getConversation(ownerId: string,id: string,sql: Sql=this.db) { const row=(await sql.query<Row>('SELECT * FROM conversations WHERE id=? AND owner_id=?',[id,ownerId]))[0]; if(!row) throw new DomainError('NOT_FOUND','Conversation not found',404); return conversation(row); }
 async activate(ownerId: string,id: string) { await this.getConversation(ownerId,id); await this.db.query('UPDATE owners SET active_conversation=? WHERE id=?',[id,ownerId]); }
 async setPetConversation(ownerId:string,id:string|null){
  await this.db.transaction(async tx=>{
   await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);
   if(id)await this.getConversation(ownerId,id,tx);
   await tx.query('UPDATE owners SET pet_conversation=? WHERE id=?',[id,ownerId]);
  });
  return {conversationId:id};
 }
 async resolvePetConversation(ownerId:string){
  return this.db.transaction(async tx=>{
   const rows=await tx.query<Row>('UPDATE owners SET created_at=created_at WHERE id=? RETURNING pet_conversation',[ownerId]);
   if(!rows.length)throw new DomainError('NOT_FOUND','Account not found',404);
   const linked=rows[0].pet_conversation as string|null;
   if(linked)return this.getConversation(ownerId,linked,tx);
   const id=randomUUID(),now=Date.now();
   await tx.query('INSERT INTO conversations(id,owner_id,title,updated_at) VALUES (?,?,?,?)',[id,ownerId,'Desktop Pet conversation',now]);
   await tx.query('UPDATE owners SET pet_conversation=? WHERE id=?',[id,ownerId]);
   return this.getConversation(ownerId,id,tx);
  });
 }
 async turns(ownerId: string,id: string) { await this.getConversation(ownerId,id); return (await this.db.query<Row>('SELECT t.*, (SELECT COALESCE(MAX(seq),0) FROM events e WHERE e.interaction_id=t.id) AS last_seq FROM turns t WHERE conversation_id=? ORDER BY ordinal LIMIT 2000',[id])).map(turn); }
 async setRepository(ownerId: string,id: string,repo: string|null) {
  await this.db.transaction(async tx=>{
   await tx.query('UPDATE conversations SET updated_at=updated_at WHERE id=? AND owner_id=?',[id,ownerId]);
   await this.getConversation(ownerId,id,tx);
   if((await tx.query('SELECT id FROM turns WHERE conversation_id=? AND status=?',[id,'running'])).length) throw new DomainError('BUSY','Stop the current response before changing repositories.',409);
   await tx.query('UPDATE conversations SET repository_id=?,updated_at=? WHERE id=?',[repo,Date.now(),id]);
  });
 }
 async setEntireState(ownerId: string,state: Owner['entireState']) { await this.db.query('UPDATE owners SET entire_state=? WHERE id=?',[state,ownerId]); }
 async begin(ctx: InteractionContext,text: string,workerId: string) {
  try { return await this.db.transaction(async tx=>{
   // Lock the conversation in both engines before selecting its next ordinal.
   await tx.query('UPDATE conversations SET updated_at=updated_at WHERE id=? AND owner_id=?',[ctx.conversationId,ctx.ownerId]);
   await this.getConversation(ctx.ownerId,ctx.conversationId,tx);
   const existing=(await tx.query<Row>('SELECT * FROM turns WHERE id=?',[ctx.interactionId]))[0];
   if(existing) { if(existing.owner_id!==ctx.ownerId || existing.conversation_id!==ctx.conversationId || existing.user_text!==text || existing.route_id!==ctx.routeId || existing.surface!==ctx.surface) throw new DomainError('IDEMPOTENCY_CONFLICT','This request ID already belongs to a different turn.',409); return {created:false,turn:turn(existing)}; }
   if((await tx.query('SELECT id FROM turns WHERE conversation_id=? AND status=?',[ctx.conversationId,'running'])).length) throw new DomainError('BUSY','Marvin is already responding in this conversation.',409);
   const n=Number((await tx.query<Row>('SELECT COALESCE(MAX(ordinal),0) AS n FROM turns WHERE conversation_id=?',[ctx.conversationId]))[0].n)+1;
   const now=Date.now();
   await tx.query('INSERT INTO turns(id,conversation_id,owner_id,ordinal,user_text,status,surface,route_id,repository_id,created_at,lease_until,worker_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)',[ctx.interactionId,ctx.conversationId,ctx.ownerId,n,text,'running',ctx.surface,ctx.routeId,ctx.repositoryId,now,now+30000,workerId]);
   await tx.query("UPDATE conversations SET updated_at=?,title=CASE WHEN title='New conversation' THEN ? ELSE title END WHERE id=?",[now,text.slice(0,64),ctx.conversationId]);
   return {created:true,turn:turn((await tx.query<Row>('SELECT * FROM turns WHERE id=?',[ctx.interactionId]))[0])};
  }); } catch(e) { if(e instanceof DomainError) throw e; if(/unique|constraint/i.test(String(e))) throw new DomainError('BUSY','Another request has already started. Refresh the conversation.',409); throw e; }
 }
 async append(event: Omit<AgentEvent,'seq'>,workerId: string) {
  return this.db.transaction(async tx=>{
   // Lease renewal fences late workers after cancellation, timeout, or takeover.
   const rows=await tx.query<Row>('UPDATE turns SET lease_until=? WHERE id=? AND status=? AND worker_id=? AND lease_until>? RETURNING id',[Date.now()+30000,event.interactionId,'running',workerId,Date.now()]);
   if(!rows.length) throw new DomainError('FENCED','This interaction is no longer active.',409);
   const seq=Number((await tx.query<Row>('SELECT COALESCE(MAX(seq),0) AS n FROM events WHERE interaction_id=?',[event.interactionId]))[0].n)+1;
   const value={...event,seq};
   await tx.query('INSERT INTO events(interaction_id,seq,event_json) VALUES (?,?,?)',[event.interactionId,seq,JSON.stringify(value)]);
   if(event.type==='delta') await tx.query('UPDATE turns SET assistant_text=assistant_text || ? WHERE id=?',[event.text??'',event.interactionId]);
   if(event.card) { const row=(await tx.query<Row>('SELECT cards FROM turns WHERE id=?',[event.interactionId]))[0]; const cards:RepoCard[]=JSON.parse(row.cards); if(cards.length<12) { cards.push(event.card); await tx.query('UPDATE turns SET cards=? WHERE id=?',[JSON.stringify(cards),event.interactionId]); } }
   const status=event.type==='completed'?'completed':event.type==='cancelled'?'cancelled':event.type==='error'?'failed':null;
   if(status) await tx.query('UPDATE turns SET status=? WHERE id=?',[status,event.interactionId]);
   return value;
  });
 }
 async heartbeat(id: string,workerId: string) { return (await this.db.query('UPDATE turns SET lease_until=? WHERE id=? AND status=? AND worker_id=? AND lease_until>? RETURNING id',[Date.now()+30000,id,'running',workerId,Date.now()])).length>0; }
 async isRunning(id: string) { return (await this.db.query('SELECT id FROM turns WHERE id=? AND status=? AND lease_until>?',[id,'running',Date.now()])).length>0; }
 async recoverExpired() { return this.db.query("UPDATE turns SET status='failed' WHERE status='running' AND lease_until<=? RETURNING id",[Date.now()]); }
 async cancel(ownerId: string,id: string) { const r=await this.db.query('UPDATE turns SET status=? WHERE id=? AND owner_id=? AND status=? RETURNING id',['cancelled',id,ownerId,'running']); return r.length>0; }
 async replay(ownerId: string,conversationId: string,routeId: string,after=0) {
  await this.getConversation(ownerId,conversationId);
  return (await this.db.query<Row>('SELECT e.event_json FROM events e JOIN turns t ON t.id=e.interaction_id WHERE t.conversation_id=? AND t.owner_id=? AND t.route_id=? AND e.seq>? ORDER BY t.ordinal,e.seq LIMIT 2000',[conversationId,ownerId,routeId,after])).map(x=>JSON.parse(x.event_json) as AgentEvent);
 }
 async saveTool(id: string,turnId: string,name: string,args: unknown,result: unknown,status: string) { await this.db.query('INSERT INTO tool_invocations(id,interaction_id,name,args_json,result_json,status,created_at) VALUES (?,?,?,?,?,?,?)',[id,turnId,name,JSON.stringify(args),JSON.stringify(result),status,Date.now()]); }
 async compact(ownerId: string,id: string,keep=8) {
  // Extractive and bounded: quote older turns, never invent a model-written memory.
  await this.db.transaction(async tx=>{
   const c=await this.getConversation(ownerId,id,tx);
   const all=await tx.query<Row>('SELECT * FROM turns WHERE conversation_id=? AND status<>? ORDER BY ordinal',[id,'running']);
   const older=all.slice(0,Math.max(0,all.length-keep)); if(!older.length) return;
   const through=older.at(-1)!.ordinal; if(through<=c.summaryThrough) return;
   const summary=older.slice(-32).map(r=>`[Turn ${r.ordinal}; surface ${r.surface}; repo ${r.repository_id??'none'}; ${r.status}] User: ${r.user_text.slice(0,350)}\nMarvin: ${r.assistant_text.slice(0,500)}`).join('\n').slice(-16000);
   await tx.query('UPDATE conversations SET summary=?,summary_through=? WHERE id=? AND summary_through<?',[summary,through,id,through]);
  });
 }
 async body(ownerId: string) { return (await this.db.query<Row>('SELECT * FROM body_slots WHERE owner_id=? AND state=?',[ownerId,'linked']))[0]??null; }
 async reserve(ownerId: string,deviceId: string,enrollmentId: string,simulated=false) {
  try { return await this.db.transaction(async tx=>{
   await tx.query("DELETE FROM body_slots WHERE state='reserved' AND expires_at<=?",[Date.now()]);
   const old=(await tx.query<Row>('SELECT * FROM body_slots WHERE enrollment_id=?',[enrollmentId]))[0];
   if(old) { if(old.owner_id===ownerId&&old.device_id===deviceId) return old; throw new DomainError('CLAIM_CONFLICT','This enrollment belongs to another device.',409); }
   await tx.query('INSERT INTO device_epochs(device_id,epoch) VALUES (?,1) ON CONFLICT(device_id) DO NOTHING',[deviceId]);
   const epoch=(await tx.query<Row>('SELECT epoch FROM device_epochs WHERE device_id=?',[deviceId]))[0].epoch;
   await tx.query('INSERT INTO body_slots(owner_id,device_id,enrollment_id,state,expires_at,epoch,simulated) VALUES (?,?,?,?,?,?,?)',[ownerId,deviceId,enrollmentId,'reserved',Date.now()+300000,epoch,simulated?1:0]);
   return (await tx.query<Row>('SELECT * FROM body_slots WHERE owner_id=?',[ownerId]))[0];
  }); } catch(e) { if(e instanceof DomainError) throw e; if(/unique|constraint/i.test(String(e))) throw new DomainError('CLAIM_CONFLICT','Only one Desktop Pet can be linked to your account, and a Desktop Pet can have only one owner.',409); throw e; }
 }
 async redeem(ownerId: string,deviceId: string,enrollmentId: string,network: string) {
  const r=await this.db.query("UPDATE body_slots SET state='linked',network=? WHERE owner_id=? AND device_id=? AND enrollment_id=? AND (state='linked' OR expires_at>?) RETURNING device_id",[network,ownerId,deviceId,enrollmentId,Date.now()]);
  if(!r.length) throw new DomainError('ENROLLMENT_EXPIRED','Setup expired. Start again.',409);
 }
 async changeNetwork(ownerId: string,deviceId: string,network: string) { const r=await this.db.query("UPDATE body_slots SET network=? WHERE owner_id=? AND device_id=? AND state='linked' RETURNING device_id",[network,ownerId,deviceId]); if(!r.length) throw new DomainError('DEVICE_MISMATCH','This is not your linked Desktop Pet.',403); }
 async unlink(ownerId: string) { return this.db.transaction(async tx=>{
  await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);
  const slot=(await tx.query<Row>('DELETE FROM body_slots WHERE owner_id=? RETURNING device_id,epoch',[ownerId]))[0];
  if(slot){
   await tx.query('UPDATE device_epochs SET epoch=epoch+1 WHERE device_id=?',[slot.device_id]);
   const replacement=Number((await tx.query<Row>('SELECT epoch FROM device_epochs WHERE device_id=?',[slot.device_id]))[0].epoch);
   await tx.query('INSERT INTO device_revocations(device_id,former_owner_id,revoked_epoch,replacement_epoch,revoked_at,acknowledged_at) VALUES (?,?,?,?,?,NULL) ON CONFLICT(device_id) DO UPDATE SET former_owner_id=excluded.former_owner_id,revoked_epoch=excluded.revoked_epoch,replacement_epoch=excluded.replacement_epoch,revoked_at=excluded.revoked_at,acknowledged_at=NULL',[slot.device_id,ownerId,Number(slot.epoch),replacement,Date.now()]);
  }
  await tx.query('INSERT INTO audit_events(id,owner_id,kind,created_at) VALUES (?,?,?,?)',[randomUUID(),ownerId,'robot_unlinked',Date.now()]);
  return slot?{deviceId:String(slot.device_id),deviceErasureConfirmed:false}:null;
 }); }
 async acknowledgeRevocation(ownerId:string,deviceId:string){const rows=await this.db.query("UPDATE device_revocations SET acknowledged_at=? WHERE device_id=? AND acknowledged_at IS NULL AND EXISTS (SELECT 1 FROM enrollment_tickets WHERE owner_id=? AND device_id=? AND operation='reconcile' AND epoch=device_revocations.revoked_epoch) RETURNING device_id",[Date.now(),deviceId,ownerId,deviceId]);return rows.length>0;}
}
