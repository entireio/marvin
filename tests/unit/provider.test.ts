import { it,expect } from 'vitest';
import { createServer,type Server } from 'node:http';
import { OpenAITextProvider,type ModelContext } from '../../packages/runtime/src/provider.js';
const ctx:ModelContext={interaction:{ownerId:'o',conversationId:'c',interactionId:'i',routeId:'r',surface:'web_text',repositoryId:'marvin-firmware',entireState:'fixture',body:{deviceId:'b',status:'online',capabilities:['head']}},summary:'Previously: remember blue.',messages:[{userText:'My color is blue',surface:'web_voice',assistantText:'Saved',status:'completed'}],input:'What changed?'};
async function listen(server:Server){await new Promise<void>(r=>server.listen(0,'127.0.0.1',r));return `http://127.0.0.1:${(server.address() as {port:number}).port}/v1`;}
const close=(s:Server)=>new Promise<void>(r=>s.close(()=>r()));
it('selects the configured repository model only for a connected real repository',async()=>{
 const models:string[]=[];const server=createServer(async(req,res)=>{let raw='';for await(const b of req)raw+=b;models.push(JSON.parse(raw).model);res.writeHead(200,{'content-type':'text/event-stream'});res.end('data: '+JSON.stringify({type:'response.completed',response:{output:[]}})+'\n\n');});
 const url=await listen(server);try{
  const provider=new OpenAITextProvider('fixture','chat-model',url,'repository-model');
  for(const entireState of ['fixture','connected','disconnected'] as const){for await(const _ of provider.run({...ctx,interaction:{...ctx.interaction,entireState}},async()=>null,AbortSignal.timeout(3000))){} }
  for await(const _ of provider.run({...ctx,interaction:{...ctx.interaction,entireState:'connected',repositoryId:null}},async()=>null,AbortSignal.timeout(3000))){}
  expect(models).toEqual(['chat-model','repository-model','chat-model','chat-model']);
 }finally{await close(server);}
});
it('real SDK adapter streams, rebuilds context, bounds tools and returns their results without provider state',async()=>{
 const requests:any[]=[];
 const server=createServer(async(req,res)=>{let raw='';for await(const b of req)raw+=b;requests.push(JSON.parse(raw));res.writeHead(200,{'content-type':'text/event-stream'});const send=(event:object)=>res.write(`data: ${JSON.stringify(event)}\n\n`);
 if(requests.length===1)send({type:'response.completed',response:{output:[{type:'function_call',name:'repository_summary',arguments:'{}',call_id:'call-1',id:'fc-1',status:'completed'}]}});
 else {send({type:'response.output_text.delta',delta:'Here is the context.'});send({type:'response.completed',response:{output:[]}});}res.end();});
 const url=await listen(server);try{const events=[];for await(const e of new OpenAITextProvider('local-test-only','test-model',url).run(ctx,async(name)=>({kind:'repository',source:'fixture',name}),new AbortController().signal))events.push(e);
 expect(events.map(e=>e.type)).toEqual(['tool','card','delta']);expect(requests).toHaveLength(2);expect(JSON.stringify(requests[0].input)).toContain('[Marvin history: web_voice]');expect(requests[0].store).toBe(false);expect(requests[0].previous_response_id).toBeUndefined();expect(JSON.stringify(requests[0].input)).toContain('remember blue');expect(requests[0].tools.map((t:any)=>t.name)).toEqual(['repository_summary']);expect(requests[1].input.at(-1).call_id).toBe('call-1');
 }finally{await close(server);}
});
for(const type of ['response.failed','response.incomplete','truncated'])it(`provider ${type} cannot falsely complete`,async()=>{const server=createServer((req,res)=>{req.resume();res.writeHead(200,{'content-type':'text/event-stream'});res.end(type==='truncated'?'':`data: ${JSON.stringify({type})}\n\n`);});const url=await listen(server);try{await expect((async()=>{for await(const _ of new OpenAITextProvider('local-test-only','test-model',url).run(ctx,async()=>null,new AbortController().signal)){} })()).rejects.toThrow();}finally{await close(server);}});
it('reserves the final round for an evidence-based answer instead of an endless retrieval loop',async()=>{
 const requests:any[]=[];let tools=0;const server=createServer(async(req,res)=>{let raw='';for await(const b of req)raw+=b;const body=JSON.parse(raw);requests.push(body);res.writeHead(200,{'content-type':'text/event-stream'});const send=(e:object)=>res.write(`data: ${JSON.stringify(e)}\n\n`);
 if(body.tool_choice==='none'){send({type:'response.output_text.delta',delta:'The retrieved evidence is incomplete.'});send({type:'response.completed',response:{output:[]}});}else send({type:'response.completed',response:{output:[{type:'function_call',name:'repository_summary',arguments:'{}',call_id:'call-'+requests.length,id:'fc-'+requests.length,status:'completed'}]}});res.end();});
 const url=await listen(server);try{let answer='';for await(const e of new OpenAITextProvider('fixture','fixture',url).run(ctx,async()=>{tools++;return {unavailable:true};},AbortSignal.timeout(3000)))if(e.type==='delta')answer+=e.text;
 expect(requests).toHaveLength(9);expect(tools).toBe(8);expect(requests[8].tool_choice).toBe('none');expect(answer).toContain('incomplete');
 }finally{await close(server);}
});
