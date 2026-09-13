import WebSocket from 'ws';
import {randomUUID} from 'node:crypto';
import type {VoiceProvider,VoiceConnection,VoiceSignal} from './voice.js';
import type {TextProvider,ModelEvent} from './provider.js';

/** Recognition and synthesis surround the canonical model execution; no second
 * model interprets tools or rewrites its answer. Each spoken turn owns its socket. */
export class DeepgramVoiceProvider implements VoiceProvider {
 constructor(private key:string,private recognitionModel:string,private synthesisModel:string,private model:TextProvider,private origin='wss://api.deepgram.com'){}
 async connect(notify:(signal:VoiceSignal)=>void):Promise<VoiceConnection>{
  const session=new Session(this.key,this.recognitionModel,this.synthesisModel,this.model,this.origin,notify);
  try{await session.open();return session;}catch{session.close();throw new Error('Speech provider connection failed');}
 }
}
class Output {
 private items:ModelEvent[]=[];private bytes=0;private wake?:()=>void;private error?:Error;private ended=false;
 push(event:ModelEvent){const bytes=JSON.stringify(event).length;if(this.ended)return;if(this.items.length>=256||this.bytes+bytes>1048576){this.fail();return;}this.items.push(event);this.bytes+=bytes;this.wake?.();}
 fail(){this.error=new Error('Speech output unavailable');this.ended=true;this.wake?.();}
 end(){this.ended=true;this.wake?.();}
 async *read(signal:AbortSignal){for(;;){signal.throwIfAborted();if(this.error)throw this.error;const value=this.items.shift();if(value){this.bytes-=JSON.stringify(value).length;yield value;continue;}if(this.ended)return;await new Promise<void>(resolve=>{const done=()=>{this.wake=undefined;signal.removeEventListener('abort',done);resolve();};this.wake=done;signal.addEventListener('abort',done,{once:true});if(signal.aborted)done();});}}
}
class Session implements VoiceConnection {
 private socket?:WebSocket;private closed=false;private epoch=0;private pending:Buffer[]=[];private pendingBytes=0;private transcript='';private lastEnd=-1;
 private keepalive:ReturnType<typeof setInterval>;private turns=new Set<AbortController>();private utterances=new Set<string>();
 constructor(private key:string,private recognitionModel:string,private synthesisModel:string,private model:TextProvider,private origin:string,private notify:(signal:VoiceSignal)=>void){
  this.keepalive=setInterval(()=>{if(this.socket?.readyState===WebSocket.OPEN)this.socket.send(JSON.stringify({type:'KeepAlive'}));},4000);
 }
 private url(path:string,model:string){const url=new URL(path,this.origin);url.search=new URLSearchParams({model,encoding:'linear16',sample_rate:'24000',...(path.endsWith('listen')?{channels:'1',interim_results:'true',vad_events:'true',endpointing:'450',utterance_end_ms:'1000'}:{})}).toString();return url;}
 private fault(){if(this.closed)return;this.close();this.notify({type:'fault',code:'VOICE_PROVIDER_UNAVAILABLE'});}
 async open(){
  if(this.closed)throw new Error('Voice closed');const epoch=++this.epoch;
  const socket=new WebSocket(this.url('/v1/listen',this.recognitionModel),{headers:{Authorization:'Token '+this.key},maxPayload:65536,handshakeTimeout:10000,followRedirects:false});this.socket=socket;
  socket.on('message',(raw,binary)=>{if(this.closed||epoch!==this.epoch)return;try{
   if(binary)throw new Error('Unexpected audio');const e=JSON.parse(raw.toString());
   if(e.type==='Error')throw new Error('Recognition failed');
   if(e.type==='SpeechStarted')this.notify({type:'speech_start'});
   if(e.type==='Results'&&e.is_final===true){
    const text=e.channel?.alternatives?.[0]?.transcript;
    if(typeof text!=='string'||!Number.isFinite(e.start)||!Number.isFinite(e.duration)||e.duration<0)throw new Error('Invalid transcript');
    const end=e.start+e.duration;
    if(end>this.lastEnd){this.lastEnd=end;this.transcript+=(this.transcript?' ':'')+text;if(this.transcript.length>12000)throw new Error('Transcript too long');}
    if(e.speech_final===true)this.finalize();
   }
   if(e.type==='UtteranceEnd')this.finalize();
  }catch{this.fault();}});
  socket.on('error',()=>{if(epoch===this.epoch)this.fault();});socket.on('close',()=>{if(epoch===this.epoch)this.fault();});
  await new Promise<void>((resolve,reject)=>{
   const timer=setTimeout(()=>{socket.terminate();reject(new Error('Recognition timeout'));},10000);
   const cleanup=()=>{clearTimeout(timer);socket.off('open',opened);socket.off('error',failed);socket.off('close',failed);};
   const failed=()=>{cleanup();reject(new Error('Recognition unavailable'));};
   const opened=()=>{cleanup();if(this.closed||epoch!==this.epoch){reject(new Error('Recognition replaced'));return;}for(const pcm of this.pending){socket.send(pcm,()=>pcm.fill(0));}this.pending=[];this.pendingBytes=0;resolve();};
   socket.once('open',opened);socket.once('error',failed);socket.once('close',failed);
  });
 }
 private finalize(){const text=this.transcript.trim();this.transcript='';if(!text)return;const itemId=randomUUID();if(this.utterances.size>=500){this.fault();return;}this.utterances.add(itemId);this.notify({type:'speech_end'});this.notify({type:'transcript',itemId,text});}
 append(pcm:Uint8Array){if(this.closed)throw new Error('Voice closed');if(!pcm.length||pcm.length%2||pcm.length>24000)throw new Error('Invalid PCM');
  if(this.socket?.readyState===WebSocket.OPEN){if(this.socket.bufferedAmount>262144){this.fault();throw new Error('Speech backpressure');}this.socket.send(pcm);}
  else{if(this.pendingBytes+pcm.length>262144){this.fault();throw new Error('Speech reconnect buffer full');}this.pending.push(Buffer.from(pcm));this.pendingBytes+=pcm.length;}
 }
 clear(){if(this.closed)return;++this.epoch;this.socket?.terminate();this.transcript='';this.lastEnd=-1;for(const p of this.pending)p.fill(0);this.pending=[];this.pendingBytes=0;const opening=this.open(),epoch=this.epoch;void opening.catch(()=>{if(epoch===this.epoch)this.fault();});}
 forTurn(itemId:string):TextProvider {
  if(!this.utterances.delete(itemId))throw new Error('Unknown or consumed utterance');const session=this;
  return {name:'deepgram-'+this.model.name,async *run(context,execute,signal){
   if(session.closed)throw new Error('Voice closed');const controller=new AbortController(),output=new Output();session.turns.add(controller);
   const abort=()=>controller.abort();signal.addEventListener('abort',abort,{once:true});if(signal.aborted)abort();
   const ws=new WebSocket(session.url('/v1/speak',session.synthesisModel),{headers:{Authorization:'Token '+session.key},maxPayload:262144,handshakeTimeout:10000,followRedirects:false});
   let completed=false,finalFlush=-1,flushes=0,lastFlushed=-1,sentText=false,characters=0;
   const cancel=()=>{output.fail();ws.terminate();};controller.signal.addEventListener('abort',cancel,{once:true});
   const timer=setTimeout(()=>controller.abort(),120000);
   ws.on('error',()=>output.fail());ws.on('close',()=>{if(!completed)output.fail();});
   ws.on('message',(raw,binary)=>{if(controller.signal.aborted||session.closed)return;try{
    if(binary){const pcm=Buffer.from(raw as Buffer);if(pcm.length%2)throw new Error('Invalid PCM');for(let i=0;i<pcm.length;i+=12000)output.push({type:'audio',pcm:pcm.subarray(i,i+12000).toString('base64')});}
    else{const e=JSON.parse(raw.toString());if(e.type==='Error')throw new Error('Synthesis failed');if(e.type==='Flushed'){if(!Number.isInteger(e.sequence_id)||e.sequence_id<0||e.sequence_id>=flushes)throw new Error('Unexpected flush');lastFlushed=Math.max(lastFlushed,e.sequence_id);if(finalFlush>=0&&lastFlushed>=finalFlush){completed=true;output.end();}}}
   }catch{output.fail();}});
   const send=(message:unknown)=>{controller.signal.throwIfAborted();if(ws.readyState!==WebSocket.OPEN||ws.bufferedAmount>65536)throw new Error('Synthesis backpressure');ws.send(JSON.stringify(message));};
   const producer=(async()=>{
    await new Promise<void>((resolve,reject)=>{const opened=()=>{cleanup();resolve();},failed=()=>{cleanup();reject(new Error('Synthesis unavailable'));},cleanup=()=>{ws.off('open',opened);ws.off('error',failed);ws.off('close',failed);};ws.once('open',opened);ws.once('error',failed);ws.once('close',failed);if(controller.signal.aborted)failed();});
    let textSinceFlush='';
    for await(const event of session.model.run(context,execute,controller.signal)){
     controller.signal.throwIfAborted();if(event.type==='audio')throw new Error('Expected canonical text model');output.push(event);
     if(event.type==='delta'&&event.text){characters+=event.text.length;if(characters>2400)throw new Error('Spoken response exceeds budget');sentText=true;textSinceFlush+=event.text;send({type:'Speak',text:event.text});
      if(flushes===0&&/[.!?]\s*$/.test(textSinceFlush)){flushes++;send({type:'Flush'});textSinceFlush='';}
     }
    }
    if(!sentText){completed=true;output.end();return;}
    finalFlush=flushes++;send({type:'Flush'});if(lastFlushed>=finalFlush){completed=true;output.end();}
   })().catch(()=>output.fail());
   try{yield* output.read(controller.signal);}finally{clearTimeout(timer);controller.abort();ws.terminate();signal.removeEventListener('abort',abort);controller.signal.removeEventListener('abort',cancel);session.turns.delete(controller);await producer;}
  }};
 }
 close(){if(this.closed)return;this.closed=true;++this.epoch;clearInterval(this.keepalive);this.socket?.terminate();for(const turn of this.turns)turn.abort();for(const p of this.pending)p.fill(0);this.pending=[];this.pendingBytes=0;this.transcript='';this.utterances.clear();}
}
