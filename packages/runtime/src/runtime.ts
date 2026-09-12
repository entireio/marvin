import { randomUUID } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { DomainError, RepoCard, type AgentEvent, type InteractionContext } from '../../contracts/src/index.js';
import { Store } from '../../persistence/src/store.js';
import { authorizeTool } from './policy.js';
import type { EntireAdapter } from './entire.js';
import type { TextProvider, ModelContext } from './provider.js';
export class Runtime {
 readonly workerId=randomUUID(); readonly events=new EventEmitter(); private active=new Map<string,AbortController>(); private tasks=new Set<Promise<void>>();
 constructor(readonly store:Store,readonly provider:TextProvider,readonly entire:EntireAdapter){this.events.setMaxListeners(200);}
 async context(ownerId:string,conversationId:string,interactionId:string,routeId:string):Promise<InteractionContext>{
  const [o,c,b]=await Promise.all([this.store.owner(ownerId),this.store.getConversation(ownerId,conversationId),this.store.body(ownerId)]);
  return {ownerId,conversationId,interactionId,routeId,surface:'web_text',repositoryId:c.repositoryId,entireState:o.entireState,body:b?{deviceId:b.device_id,status:'offline',capabilities:[],simulated:!!b.simulated,network:b.network}:null};
 }
 async start(ctx:InteractionContext,text:string){
  const started=await this.store.begin(ctx,text,this.workerId);if(!started.created)return started;
  const controller=new AbortController();this.active.set(ctx.interactionId,controller);
  const task=this.execute(ctx,text,controller).finally(()=>{this.active.delete(ctx.interactionId);this.tasks.delete(task);});this.tasks.add(task);
  return started;
 }
 async cancel(ownerId:string,id:string){const changed=await this.store.cancel(ownerId,id);if(changed)this.active.get(id)?.abort();return changed;}
 async execute(ctx:InteractionContext,input:string,controller:AbortController){
  const timeout=setTimeout(()=>controller.abort(new Error('Response timed out')),120000);
  const heartbeat=setInterval(()=>{void this.store.heartbeat(ctx.interactionId,this.workerId).then(ok=>{if(!ok)controller.abort();}).catch(()=>controller.abort());},5000);
  const emit=async (event:Pick<AgentEvent,'type'|'text'|'card'|'code'>)=>{const value=await this.store.append({v:1,interactionId:ctx.interactionId,conversationId:ctx.conversationId,routeId:ctx.routeId,...event},this.workerId);this.events.emit('event',value,ctx.ownerId);};
  try{
   await emit({type:'started'});
   const c=await this.store.getConversation(ctx.ownerId,ctx.conversationId), turns=await this.store.turns(ctx.ownerId,ctx.conversationId);
   let remaining=24000;
   const messages=turns.filter(t=>t.id!==ctx.interactionId&&t.ordinal>c.summaryThrough).reverse().filter(t=>{const size=t.userText.length+t.assistantText.length;remaining-=size;return remaining>=0;}).reverse();
   const modelContext:ModelContext={interaction:ctx,summary:c.summary,messages,input};
   const execute=async(name:string,args:unknown)=>{
    controller.signal.throwIfAborted();if(!await this.store.isRunning(ctx.interactionId))throw new DomainError('FENCED','Interaction ended',409);
    const fresh=await this.context(ctx.ownerId,ctx.conversationId,ctx.interactionId,ctx.routeId);
    authorizeTool({...fresh,surface:ctx.surface},name,args);
    const id=randomUUID();
    try{
     if(name!=='repository_summary'||!ctx.repositoryId)throw new DomainError('UNSUPPORTED','Physical execution is not implemented in M3.',403);
     const card=RepoCard.parse(await this.entire.readSummary(ctx.repositoryId));await this.store.saveTool(id,ctx.interactionId,name,args,card,'completed');return card;
    }catch(e){await this.store.saveTool(id,ctx.interactionId,name,args,{error:'TOOL_UNAVAILABLE'},'failed');throw e;}
   };
   let chars=0;
   for await(const event of this.provider.run(modelContext,execute,controller.signal)){
    if(event.type==='delta'){chars+=event.text.length;if(chars>64000)throw new Error('Response size limit reached.');}
    await emit(event);
   }
   await emit({type:'completed'});await this.store.compact(ctx.ownerId,ctx.conversationId);
  }catch{
   try{if(await this.store.isRunning(ctx.interactionId))await emit({type:'error',code:controller.signal.aborted?'INTERRUPTED':'PROVIDER_ERROR',text:controller.signal.aborted?'The response was interrupted. You can continue here.':'Marvin could not finish this response. Your conversation is saved; please try again.'});}catch{/* Another worker or cancellation already finalized the turn. */}
   this.events.emit('refresh',ctx.conversationId,ctx.ownerId);
  }finally{clearTimeout(timeout);clearInterval(heartbeat);}
 }
 async drain(){await Promise.all([...this.tasks]);}
 async close(){for(const c of this.active.values())c.abort();await this.drain();}
}
