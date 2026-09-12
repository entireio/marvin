import { describe,it,expect } from 'vitest';
import { randomUUID } from 'node:crypto';
import { sqliteDatabase } from '../../packages/persistence/src/database.js';
import { migrate } from '../../packages/persistence/src/migrations.js';
import { Store } from '../../packages/persistence/src/store.js';
import { Runtime } from '../../packages/runtime/src/runtime.js';
import { FixtureTextProvider,type TextProvider,type ModelContext } from '../../packages/runtime/src/provider.js';
import { FixtureEntire } from '../../packages/runtime/src/entire.js';
import { allowedTools,authorizeTool,bodyExplanation } from '../../packages/runtime/src/policy.js';
import { SendTurn,ClientEvent,BodyHello,ScanResult,type InteractionContext } from '../../packages/contracts/src/index.js';
const base:InteractionContext={ownerId:'owner',conversationId:'thread',interactionId:'turn',routeId:'route',surface:'web_text',repositoryId:'marvin-firmware',entireState:'fixture',body:null};
describe('capability policy',()=>{
 for(const surface of ['web_text','web_voice'] as const)for(const body of [null,{deviceId:'body',status:'offline' as const,capabilities:['head']},{deviceId:'body',status:'online' as const,capabilities:['head']}])it(`${surface} never exposes or dispatches physical tools (${body?.status??'absent'})`,()=>{const ctx={...base,surface,body};expect(allowedTools(ctx).map(t=>t.name)).toEqual(['repository_summary']);expect(()=>authorizeTool(ctx,'physical_head',{})).toThrow();expect(bodyExplanation(ctx).length).toBeGreaterThan(20);});
 it('body capability is necessary and repository writes are absent everywhere',()=>{const ctx={...base,surface:'body_voice' as const,body:{deviceId:'body',status:'online' as const,capabilities:['head']}};expect(allowedTools(ctx).map(t=>t.name)).toContain('physical_head');for(const surface of ['web_text','web_voice','body_voice'] as const)expect(()=>authorizeTool({...ctx,surface},'repository_write',{})).toThrow();expect(()=>authorizeTool({...ctx,body:{...ctx.body,capabilities:[]}},'physical_head',{})).toThrow();});
});
it('schemas reject spoofed surfaces, HTML control commands, oversized text and unsupported device versions',()=>{expect(SendTurn.safeParse({conversationId:'c',interactionId:'i',routeId:'r',text:'hello',surface:'body_voice'}).success).toBe(false);expect(SendTurn.safeParse({conversationId:'c',interactionId:'i',routeId:'r',text:'x'.repeat(12001)}).success).toBe(false);expect(ClientEvent.safeParse({v:1,type:'physical_head'}).success).toBe(false);expect(BodyHello.safeParse({v:2,type:'hello',deviceId:'body',firmware:'1',capabilities:[]}).success).toBe(false);expect(ScanResult.safeParse({v:1,source:'computer',deviceId:'b',scanId:'s',scannedAt:0,networks:[]}).success).toBe(false);});
it('20 continuity cases survive runtime reconstruction, compaction and repository changes',async()=>{
 const db=sqliteDatabase();await migrate(db);const store=new Store(db);const o=await store.ensureOwner('test','continuity','Owner');
 const seen:ModelContext[]=[];const provider:TextProvider={name:'capture',async *run(context){seen.push(context);yield{type:'delta',text:'I have saved the supplied context.'};}};
 try{for(let i=0;i<20;i++){
  const c=await store.createConversation(o.id);let runtime=new Runtime(store,provider,new FixtureEntire());
  for(let j=0;j<12;j++){const ctx=await runtime.context(o.id,c.id,randomUUID(),'route');await runtime.start(ctx,`Case ${i} fact ${j}: subsystem-${i}-${j}`);await runtime.drain();}
  await store.compact(o.id,c.id,4);await runtime.close();
  await store.setRepository(o.id,c.id,'marvin-portal');runtime=new Runtime(new Store(db),provider,new FixtureEntire());
  const ctx=await runtime.context(o.id,c.id,randomUUID(),'new-browser');await runtime.start(ctx,'Recall the earlier facts.');await runtime.drain();
  const last=seen.at(-1)!;expect(last.summary).toContain(`Case ${i} fact 0`);expect(last.messages.some(m=>m.userText.includes(`Case ${i} fact 11`))).toBe(true);expect(last.interaction.repositoryId).toBe('marvin-portal');expect(last.interaction.routeId).toBe('new-browser');await runtime.close();
 }}finally{await db.close();}
});
it('fixture tool calls are persisted and disconnected Entire is not exposed',async()=>{
 const db=sqliteDatabase();await migrate(db);const store=new Store(db);const o=await store.ensureOwner('test','tools','Owner'),c=await store.createConversation(o.id);await store.setEntireState(o.id,'fixture');await store.setRepository(o.id,c.id,'marvin-firmware');const rt=new Runtime(store,new FixtureTextProvider(0),new FixtureEntire());try{const ctx=await rt.context(o.id,c.id,randomUUID(),'route');await rt.start(ctx,'What changed in the firmware?');await rt.drain();expect((await store.turns(o.id,c.id))[0].cards[0].source).toBe('fixture');expect(await db.query('SELECT id FROM tool_invocations')).toHaveLength(1);await store.setEntireState(o.id,'disconnected');expect(allowedTools(await rt.context(o.id,c.id,'new','route'))).toHaveLength(0);}finally{await rt.close();await db.close();}
});
