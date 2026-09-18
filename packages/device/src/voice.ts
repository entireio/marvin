import {randomUUID} from 'node:crypto';
import {DomainError} from '../../contracts/src/index.js';
import type {Runtime} from '../../runtime/src/runtime.js';
import type {VoiceProvider,VoiceConnection,VoiceSignal} from '../../runtime/src/voice.js';
import type {DeviceIdentity} from './store.js';
import {Pcm16To24} from './pcm-rate.js';
export type DeviceVoiceSink={control:(event:unknown)=>Promise<void>;audio:(id:string,pcm:Buffer)=>Promise<void>};
type Session={identity:DeviceIdentity;connection?:VoiceConnection;sink:DeviceVoiceSink;closed:boolean;oneShot:boolean;followupMs:number;generation:number;activeId:string;lastId:string;conversationId:string;lastActivity:number;started:number;turnFinishedAt:number;awaitingPlaybackId:string;playhead:number;wakeStopUntil:number;seen:Set<string>;queue:Promise<void>;resampler?:Pcm16To24};
const isStopCommand=(text:string)=>/^(?:hey marvin |marvin )?stop$/.test(text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu,' ').trim());
/** A wake-triggered body session accepts follow-up turns for a bounded window. */
export class DeviceVoice {
 private sessions=new Map<string,Session>();private timer:ReturnType<typeof setInterval>;
 constructor(readonly runtime:Runtime,readonly provider?:VoiceProvider){this.timer=setInterval(()=>{const now=Date.now();for(const s of this.sessions.values())if((s.oneShot&&s.turnFinishedAt&&now-s.turnFinishedAt>=s.followupMs)||(!s.activeId&&now-s.lastActivity>90000)||now-s.started>900000)void this.stop(s.identity.deviceId);},100);}
 async start(identity:DeviceIdentity,sink:DeviceVoiceSink,inputSampleRate:16000|24000=24000,oneShot=false,followupSeconds=5){
  if(!this.provider)throw new DomainError('VOICE_UNAVAILABLE','Voice is not configured. Presence remains available.',409);
  if(this.sessions.has(identity.deviceId))return;
  if(this.sessions.size>=32)throw new DomainError('VOICE_BUSY','Voice server is busy. Try again shortly.',429);
  const s:Session={identity,sink,closed:false,oneShot,followupMs:Math.max(0,Math.min(30,followupSeconds))*1000,generation:0,activeId:'',lastId:'',conversationId:'',lastActivity:Date.now(),started:Date.now(),turnFinishedAt:0,awaitingPlaybackId:'',playhead:0,wakeStopUntil:0,seen:new Set(),queue:Promise.resolve(),...(inputSampleRate===16000?{resampler:new Pcm16To24()}:{})};this.sessions.set(identity.deviceId,s);
  try{const c=await this.runtime.store.resolvePetConversation(identity.ownerId);s.conversationId=c.id;if(!this.current(s))return;this.runtime.events.emit('conversation_link',identity.ownerId);
   const connection=await this.provider.connect(e=>this.signal(s,e));if(s.closed){connection.close();return;}s.connection=connection;await sink.control({type:'voice_ready',sampleRate:24000,inputSampleRate,channels:1,format:'s16le'});
  }catch(e){await this.stopSession(s);throw e;}
 }
 active(deviceId:string){return this.sessions.has(deviceId);}
 setFollowupSeconds(deviceId:string,seconds:number){const s=this.sessions.get(deviceId);if(s)s.followupMs=seconds*1000;}
 playbackDone(deviceId:string,id:string){const s=this.sessions.get(deviceId);if(s&&s.awaitingPlaybackId===id){s.awaitingPlaybackId='';s.turnFinishedAt=Date.now();s.lastActivity=Date.now();}}
 async announce(identity:DeviceIdentity,sink:DeviceVoiceSink,text:string){
  if(!this.provider||this.sessions.has(identity.deviceId))return false;const connection=await this.provider.connect(()=>{});if(!connection.speak){connection.close();return false;}
  const id=randomUUID(),controller=new AbortController();let playhead=performance.now();
  try{await sink.control({type:'voice_announcement',interactionId:id,sampleRate:24000,channels:1,format:'s16le'});
   for await(const pcm of connection.speak(text,controller.signal)){const bytes=Buffer.from(pcm);if(!bytes.length||bytes.length%2||bytes.length>12000)throw new Error('Invalid announcement audio');for(let at=0;at<bytes.length;at+=3840){const delay=playhead-performance.now()-160;if(delay>0)await new Promise(resolve=>setTimeout(resolve,delay));const frame=bytes.subarray(at,at+3840);playhead=Math.max(playhead,performance.now())+frame.length/48;await sink.audio(id,frame);}}
   await sink.control({type:'voice_turn_end',interactionId:id});await new Promise(resolve=>setTimeout(resolve,750));await sink.control({type:'voice_closed'});return true;
  }catch{await sink.control({type:'audio_flush',interactionId:id}).catch(()=>{});await sink.control({type:'voice_closed'}).catch(()=>{});return false;}finally{controller.abort();connection.close();}
 }
 append(deviceId:string,pcm:Buffer){const s=this.sessions.get(deviceId);if(!s||!s.connection||s.closed)throw new DomainError('AUDIO_NOT_ACTIVE','Start voice before uploading audio.',403);if(!pcm.length||pcm.length>4096||pcm.length%2)throw new DomainError('AUDIO_INVALID','Send mono s16le PCM in bounded frames.',400);s.connection.append(s.resampler?s.resampler.convert(pcm):pcm);}
 private current(s:Session){return !s.closed&&this.sessions.get(s.identity.deviceId)===s;}
 private async flush(s:Session){if(!this.current(s))return;const id=s.activeId||s.lastId;s.activeId='';s.awaitingPlaybackId='';s.turnFinishedAt=0;s.playhead=0;await s.sink.control({type:'audio_flush',interactionId:id||null});if(id){await this.runtime.cancel(s.identity.ownerId,id);await this.runtime.waitTurn(id);}}
 async interrupt(deviceId:string,reason:'wake'|'manual'|'speech'='manual'){const s=this.sessions.get(deviceId);if(!s)return;s.generation++;if(reason==='wake')s.wakeStopUntil=Date.now()+10000;else if(reason==='manual')s.wakeStopUntil=0;await this.flush(s);}
 private signal(s:Session,e:VoiceSignal){if(!this.current(s))return;s.lastActivity=Date.now();
  if(e.type==='fault'){void this.stopSession(s);return;}
  if(e.type==='speech_start'){s.turnFinishedAt=0;s.awaitingPlaybackId='';void this.interrupt(s.identity.deviceId,'speech').catch(()=>this.stopSession(s));return;}
  if(e.type!=='transcript'||s.seen.has(e.itemId))return;s.seen.add(e.itemId);if(s.seen.size>500){void this.stopSession(s);return;}
  const generation=s.generation;s.turnFinishedAt=0;
  s.queue=s.queue.then(async()=>{const stale=()=>!this.current(s)||s.generation!==generation;if(stale()||!s.connection)return;const text=e.text.trim();const stop=s.wakeStopUntil>Date.now()&&isStopCommand(text);s.wakeStopUntil=0;if(stop){await this.stopSession(s);return;}await this.flush(s);if(stale())return;if(!text)return;if(text.length>12000)throw new Error('Spoken turn too long');
   const id=randomUUID();const context={...await this.runtime.context(s.identity.ownerId,s.conversationId,id,s.identity.deviceId),surface:'body_voice' as const};
   if(stale())return;
   if(context.body?.deviceId!==s.identity.deviceId||context.body.status!=='online')throw new Error('Device ownership or presence changed');
   s.activeId=s.lastId=id;await s.sink.control({type:'voice_turn',interactionId:id,text});if(stale())return;
   await this.runtime.start(context,text,{provider:s.connection.forTurn(e.itemId),audio:async pcm=>{if(s.closed||s.activeId!==id)return;const bytes=Buffer.from(pcm,'base64');if(bytes.length%2||bytes.length>480000)throw new Error('Invalid provider audio');for(let i=0;i<bytes.length;i+=3840){if(s.closed||s.activeId!==id)return;
    // Keep provider bursts within a small lead over physical playback. Awaiting
    // each frame bounds transport pressure and keeps interruption responsive.
    const delay=s.playhead-performance.now()-160;if(delay>0)await new Promise(resolve=>setTimeout(resolve,delay));
    if(!this.current(s)||s.activeId!==id||s.generation!==generation)return;
    const frame=bytes.subarray(i,i+3840);s.playhead=Math.max(s.playhead,performance.now())+frame.length/48;
    await s.sink.audio(id,frame);}}});
   if(stale()){await this.runtime.cancel(s.identity.ownerId,id);await this.runtime.waitTurn(id);return;}
   // Keep lastId for playback flush even after generation finishes.
   await this.runtime.waitTurn(id);if(!s.closed&&s.activeId===id){s.activeId='';const playbackRemaining=Math.max(0,s.playhead-performance.now());s.playhead=0;s.awaitingPlaybackId=id;await s.sink.control({type:'voice_turn_end',interactionId:id});
    // Older firmware has no playback acknowledgement. Never cut off its queued audio.
    setTimeout(()=>{if(this.current(s)&&s.awaitingPlaybackId===id){s.awaitingPlaybackId='';s.turnFinishedAt=Date.now();}},Math.ceil(playbackRemaining)+500);}
  }).catch(()=>this.stopSession(s));
 }
 async stop(deviceId:string){const s=this.sessions.get(deviceId);if(s)await this.stopSession(s);}
 private async stopSession(s:Session){if(!this.current(s))return;const deviceId=s.identity.deviceId;s.closed=true;s.generation++;s.resampler?.clear();this.sessions.delete(deviceId);try{s.connection?.close();}catch{/* Continue local cleanup after provider transport failure. */}if(s.activeId)await this.runtime.cancel(s.identity.ownerId,s.activeId).catch(()=>{});await s.sink.control({type:'audio_flush',interactionId:s.activeId||s.lastId||null}).catch(()=>{});await s.sink.control({type:'voice_closed'}).catch(()=>{});}
 async close(){clearInterval(this.timer);await Promise.all([...this.sessions.keys()].map(id=>this.stop(id)));}
}
