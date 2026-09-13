import {Store} from './store.js';
import {DomainError} from '../../contracts/src/index.js';
/** Explicit allowlist: never export session, device, setup, or provider credentials. */
export async function exportPage(store:Store,ownerId:string,after:string|undefined,cutoff=Date.now()){
 const anchor=after?(await store.db.query<{created_at:number;id:string}>('SELECT id,created_at FROM turns WHERE id=? AND owner_id=?',[after,ownerId]))[0]:null;
 if(after&&!anchor)throw new DomainError('CURSOR_INVALID','Export cursor is unavailable.',400);
 const rows=await store.db.query<{id:string;created_at:number}>('SELECT id,conversation_id,user_text,assistant_text,status,surface,created_at,ordinal FROM turns WHERE owner_id=? AND created_at<=? AND (created_at>? OR (created_at=? AND id>?)) ORDER BY created_at,id LIMIT 21',[ownerId,cutoff,anchor?.created_at??0,anchor?.created_at??0,anchor?.id??'']);
 const turns=rows.slice(0,20),owner=await store.owner(ownerId);
 return {format:'marvin-export-v1',exportedAt:Date.now(),owner:{id:owner.id,name:owner.name},turns,nextCursor:rows.length>20?turns.at(-1)!.id:null};
}
/** Call after cancelling active work. Lock owner against concurrent enrollment/turn starts. */
export async function deleteAccount(store:Store,ownerId:string){
 await store.db.transaction(async tx=>{
  await tx.query('UPDATE owners SET created_at=created_at WHERE id=?',[ownerId]);
  await tx.query('UPDATE device_epochs SET epoch=epoch+1 WHERE device_id IN (SELECT device_id FROM body_slots WHERE owner_id=?)',[ownerId]);
  for(const table of ['events','tool_invocations'])await tx.query(`DELETE FROM ${table} WHERE interaction_id IN (SELECT id FROM turns WHERE owner_id=?)`,[ownerId]);
  for(const table of ['device_credentials','device_commands','enrollment_tickets','body_slots','audit_events','sessions','turns','conversations'])await tx.query(`DELETE FROM ${table} WHERE owner_id=?`,[ownerId]);
  await tx.query('DELETE FROM owners WHERE id=?',[ownerId]);
 });
}

export async function purgeExpiredHistory(store:Store,ownerId:string,cutoff:number){
 return store.db.transaction(async tx=>{
  const selected=await tx.query<{id:string;conversation_id:string}>("SELECT id,conversation_id FROM turns WHERE owner_id=? AND created_at<? AND status<>'running' ORDER BY created_at,id LIMIT 1000",[ownerId,cutoff]);if(!selected.length)return 0;
  const ids=selected.map(t=>t.id),marks=ids.map(()=>'?').join(',');
  for(const table of ['events','tool_invocations'])await tx.query(`DELETE FROM ${table} WHERE interaction_id IN (${marks})`,ids);
  await tx.query(`DELETE FROM turns WHERE id IN (${marks})`,ids);
  const conversations=[...new Set(selected.map(t=>t.conversation_id))];for(const id of conversations)await tx.query("UPDATE conversations SET summary='',summary_through=0 WHERE id=? AND owner_id=?",[id,ownerId]);return ids.length;
 });
}
