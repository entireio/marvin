import { csrf } from './api';
import type { VoiceState } from '../../../packages/contracts/src/voice';
export type VoiceView={state:VoiceState;message?:string;interactionId?:string;userText?:string;assistantText?:string};
/** Audio is never broadcast or stored. A fresh socket owns a fresh output route. */
export class BrowserVoice {
 private socket?:WebSocket;private stream?:MediaStream;private context?:AudioContext;private worklet?:AudioWorkletNode;private source?:MediaStreamAudioSourceNode;
 private sources=new Set<AudioBufferSourceNode>();private pendingAudio:{pcm:string;seq?:number}[]=[];private captureQueue:ArrayBuffer[]=[];private next=0;private closed=false;private muted=false;private ready=false;private turn='';private blocked=new Set<string>();private transcript='';private serverState:VoiceState='connecting';private timer?:ReturnType<typeof setTimeout>;private pumpTimer?:ReturnType<typeof setTimeout>;private captureTimer?:ReturnType<typeof setTimeout>;private reconnectTimer?:ReturnType<typeof setTimeout>;private reconnects=0;private inputSequence=0;private inputDrops=0;private outputSequence=0;private outputGaps=0;private targetLead=.12;private lastAudioAt=0;
 constructor(private conversationId:string,private notify:(view:VoiceView)=>void,private refresh:()=>void){}
 private state(state:VoiceState,message?:string){if(!this.closed)this.notify({state,message});}
 async start(){
  try{
   if(!navigator.mediaDevices?.getUserMedia||!window.AudioWorkletNode)throw new Error('Voice needs a browser with microphone and AudioWorklet support, on HTTPS or localhost.');
   this.state('connecting','Allow your microphone to start voice.');
   const stream=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true,autoGainControl:true},video:false});
   if(this.closed){stream.getTracks().forEach(t=>t.stop());return;}this.stream=stream;
   stream.getAudioTracks().forEach(t=>t.addEventListener('ended',()=>this.fail('Your microphone was disconnected. Reconnect it, then try again.')));
   const context=new AudioContext({sampleRate:24000,latencyHint:'interactive'});this.context=context;await context.resume();if(context.sampleRate!==24000)throw new Error('This browser cannot use the required audio format. Try a current Chrome or Edge browser.');
   await context.audioWorklet.addModule('/audio-capture.js');if(this.closed)return;
   const node=new AudioWorkletNode(context,'marvin-capture');this.worklet=node;this.source=context.createMediaStreamSource(stream);this.source.connect(node);const silence=context.createGain();silence.gain.value=0;node.connect(silence).connect(context.destination);
   node.port.onmessage=e=>{if(this.closed||this.muted)return;if(e.data.type==='speech'){if(this.ready){this.interrupt();this.state('hearing');}}else if(e.data.type==='pcm')this.enqueueCapture(e.data.bytes);};
   this.timer=setTimeout(()=>this.fail('Voice took too long to connect. Check your connection and retry.'),16000);
   this.connectSocket();
  }catch(e){const name=(e as Error).name;this.fail(name==='NotAllowedError'?'Microphone access was not allowed. Enable it in your browser’s site settings, then try again.':name==='NotFoundError'?'No microphone was found. Connect a microphone and try again.':(e as Error).message);}
 }
 private connectSocket(){
  if(this.closed)return;const socket=new WebSocket(`${location.protocol==='https:'?'wss:':'ws:'}//${location.host}/api/voice`);this.socket=socket;
  socket.onopen=()=>this.send({type:'start',conversationId:this.conversationId,csrf});
  socket.onmessage=e=>{let message:Record<string,any>;try{message=JSON.parse(String(e.data));}catch{this.fail('Voice received an invalid response. Reconnect to continue.');return;}try{this.receive(message);}catch(error){this.fail(`Voice audio could not be played. ${error instanceof Error?error.message:'Reconnect to continue.'}`);}};
  socket.onerror=()=>{};
  socket.onclose=()=>{if(!this.closed)this.retry();};
 }
 private retry(){
  if(this.closed||this.reconnectTimer)return;this.ready=false;if(this.reconnects>=5){this.fail('Voice could not reconnect. Your conversation is saved.');return;}
  const delay=Math.min(8000,400*2**this.reconnects++);this.state('connecting','Reconnecting voice…');this.reconnectTimer=setTimeout(()=>{this.reconnectTimer=undefined;this.connectSocket();},delay);
 }
 private enqueueCapture(bytes:ArrayBuffer){
  const payload=new Uint8Array(bytes),frame=new Uint8Array(payload.length+12),view=new DataView(frame.buffer);frame.set([77,86,66,49]);view.setUint32(4,++this.inputSequence);view.setUint32(8,Math.floor(performance.now())>>>0);frame.set(payload,12);this.captureQueue.push(frame.buffer);
  // Live speech is valuable only while current. Keep a small cushion through a
  // transient stall and discard oldest audio rather than ending the session or
  // making the provider transcribe seconds-old speech.
  while(this.captureQueue.length>25){this.captureQueue.shift();this.inputDrops++;}this.flushCapture();
 }
 private flushCapture(){
  clearTimeout(this.captureTimer);if(this.closed||!this.ready||this.socket?.readyState!==WebSocket.OPEN)return;
  while(this.captureQueue.length&&this.socket.bufferedAmount<48000)this.socket.send(this.captureQueue.shift()!);
  if(this.captureQueue.length)this.captureTimer=setTimeout(()=>this.flushCapture(),20);
 }
 private send(event:object){if(this.socket?.readyState===WebSocket.OPEN)this.socket.send(JSON.stringify({v:1,...event}));}
 private receive(e:Record<string,any>){
  if(this.closed||e.v!==1)return;
  if(e.type==='ready'){if(e.sampleRate!==24000)throw new Error('Invalid format');clearTimeout(this.timer);this.ready=true;this.reconnects=0;this.state('listening');this.flushCapture();}
  if(e.type==='state'){this.serverState=e.state;if(e.state==='hearing')this.flush();this.state(this.muted?'muted':this.sources.size?'speaking':e.state);}
  if(e.type==='turn'){this.turn=e.interactionId;this.transcript='';this.notify({state:this.muted?'muted':'thinking',interactionId:e.interactionId,userText:e.text,assistantText:''});this.refresh();}
  if(e.type==='audio'&&e.interactionId===this.turn&&!this.blocked.has(this.turn)&&!this.muted)this.queueAudio(e.pcm,e.seq);
  if(e.type==='flush')this.flush();
  if(e.type==='event'){
   const event=e.event;if(event.interactionId!==this.turn)return;
   if(event.type==='delta'){this.transcript+=event.text??'';this.notify({state:this.muted?'muted':this.sources.size?'speaking':'thinking',assistantText:this.transcript});}
   if(['completed','cancelled','error','card'].includes(event.type))this.refresh();
   if(event.type==='error'){this.flush();this.state('listening','That response was interrupted. You can try again.');}
  }
  if(e.type==='refresh')this.refresh();
  if(e.type==='error')this.fail(e.message??'Voice is unavailable.');
  if(e.type==='closed'){const message=String(e.message??'Voice ended.');this.stop();this.notify({state:'closed',message});}
 }
 private queueAudio(pcm:string,seq?:number){
  if(typeof pcm!=='string'||pcm.length>24000)throw new Error('Received an oversized audio packet.');
  // The server normally paces audio, but network delivery can still arrive in bursts.
  // Keep those bursts out of the AudioContext schedule instead of treating them as a bad response.
  if(typeof seq==='number'){if(this.outputSequence&&seq>this.outputSequence+1){this.outputGaps+=seq-this.outputSequence-1;this.targetLead=Math.min(.8,this.targetLead+.06);}if(seq<=this.outputSequence)return;this.outputSequence=seq;}
  if(this.pendingAudio.length>=256){this.pendingAudio.shift();this.targetLead=Math.min(.8,this.targetLead+.06);}this.pendingAudio.push({pcm,seq});this.lastAudioAt=performance.now();this.pumpAudio();
 }
 private pumpAudio(){
  clearTimeout(this.pumpTimer);if(this.closed||this.muted||!this.pendingAudio.length)return;
  const ctx=this.context;if(!ctx||ctx.state==='closed')return;
  // Start with a small, adaptive cushion. It masks ordinary network jitter
  // without turning reliable-network playback into a visibly delayed stream.
  if(this.next<=ctx.currentTime&&this.pendingAudio[0]?.seq!==undefined&&this.pendingAudio.length<2&&performance.now()-this.lastAudioAt<Math.ceil(this.targetLead*1000)){this.pumpTimer=setTimeout(()=>this.pumpAudio(),Math.max(10,this.targetLead*1000));return;}
  while(this.pendingAudio.length&&this.next-ctx.currentTime<Math.max(this.targetLead,.2))this.play(this.pendingAudio.shift()!.pcm);
  if(this.pendingAudio.length){const delay=Math.max(10,(this.next-ctx.currentTime-Math.max(0,this.targetLead-.05))*1000);this.pumpTimer=setTimeout(()=>this.pumpAudio(),delay);}
 }
 private play(pcm:string){
  if(typeof pcm!=='string'||pcm.length>24000)throw new Error('Oversized audio');const raw=atob(pcm);if(raw.length%2)throw new Error('Invalid PCM');const ctx=this.context;if(!ctx||ctx.state==='closed')return;
  const data=new Float32Array(raw.length/2);for(let i=0;i<data.length;i++){let v=raw.charCodeAt(2*i)|(raw.charCodeAt(2*i+1)<<8);if(v>=32768)v-=65536;data[i]=v/32768;}
  const buffer=ctx.createBuffer(1,data.length,24000);buffer.copyToChannel(data,0);const source=ctx.createBufferSource();source.buffer=buffer;source.connect(ctx.destination);this.sources.add(source);source.onended=()=>{this.sources.delete(source);source.disconnect();this.pumpAudio();if(!this.sources.size&&!this.pendingAudio.length&&!this.closed)this.state(this.muted?'muted':this.serverState==='thinking'?'thinking':'listening');};
  const when=Math.max(ctx.currentTime+.015,this.next);source.start(when);this.next=when+buffer.duration;this.state('speaking');
 }
 private flush(){clearTimeout(this.pumpTimer);this.pendingAudio=[];for(const source of this.sources){source.onended=null;try{source.stop();}catch{}source.disconnect();}this.sources.clear();this.next=this.context?.currentTime??0;}
 interrupt(){if(this.turn){this.blocked.add(this.turn);if(this.blocked.size>500)this.blocked.delete(this.blocked.values().next().value!);}this.flush();this.send({type:'interrupt'});}
 mute(value:boolean){this.muted=value;this.stream?.getAudioTracks().forEach(t=>{t.enabled=!value;});this.worklet?.port.postMessage({muted:value});if(value)this.interrupt();this.send({type:'mute',muted:value});this.state(value?'muted':'listening');}
 stop(){if(this.closed)return;this.send({type:'stop'});this.closed=true;this.ready=false;clearTimeout(this.timer);clearTimeout(this.captureTimer);clearTimeout(this.reconnectTimer);this.captureQueue=[];this.flush();this.worklet?.disconnect();this.source?.disconnect();if(this.worklet)this.worklet.port.onmessage=null;this.stream?.getTracks().forEach(t=>t.stop());void this.context?.close().catch(()=>{});this.socket?.close();this.refresh();}
 private fail(message:string){if(this.closed)return;this.stop();this.notify({state:'error',message});}
}
