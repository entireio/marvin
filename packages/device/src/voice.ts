import {randomUUID} from 'node:crypto';
import {DomainError} from '../../contracts/src/index.js';
import type {Runtime} from '../../runtime/src/runtime.js';
import type {VoiceProvider,VoiceConnection,VoiceSignal} from '../../runtime/src/voice.js';
import type {DeviceIdentity} from './store.js';
import {Pcm16To24} from './pcm-rate.js';
export type DeviceVoiceSink={control:(event:unknown)=>Promise<void>;audio:(id:string,pcm:Buffer)=>Promise<void>};
type Session={identity:DeviceIdentity;connection?:VoiceConnection;sink:DeviceVoiceSink;closed:boolean;oneShot:boolean;followupMs:number;generation:number;activeId:string;lastId:string;conversationId:string;lastActivity:number;started:number;turnFinishedAt:number;awaitingPlaybackId:string;outputStarted:boolean;playhead:number;wakeStopUntil:number;seen:Set<string>;queue:Promise<void>;resampler?:Pcm16To24};
type AnnouncementJob={id:string;identity:DeviceIdentity;sink:DeviceVoiceSink;text:string;preemptConversation:boolean;resolve:(spoken:boolean)=>void};
type AnnouncementQueue={items:AnnouncementJob[];running:boolean;controller?:AbortController};
// Allow for ordinary Wi-Fi/TLS jitter before the speaker exhausts its queue.
// Audio still starts immediately; this only controls how far ahead subsequent
// frames are sent.
const playbackHeadroomMs=400;
const announcementGapMs=250;
const isStopCommand=(text:string)=>/^(?:hey marvin |marvin )?stop$/.test(text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu,' ').trim());
/** A wake-triggered body session accepts follow-up turns for a bounded window. */
export class DeviceVoice {
 private sessions=new Map<string,Session>();private announcements=new Map<string,AnnouncementQueue>();private playbackWaiters=new Map<string,Set<()=>void>>();private timer:ReturnType<typeof setInterval>;
 constructor(readonly runtime:Runtime,readonly provider?:VoiceProvider){this.timer=setInterval(()=>{const now=Date.now();for(const s of this.sessions.values())if((s.oneShot&&s.turnFinishedAt&&now-s.turnFinishedAt>=s.followupMs)||(!s.activeId&&now-s.lastActivity>90000)||now-s.started>900000)void this.stop(s.identity.deviceId);},100);}
 async start(identity:DeviceIdentity,sink:DeviceVoiceSink,inputSampleRate:16000|24000=24000,oneShot=false,followupSeconds=5){
  if(!this.provider)throw new DomainError('VOICE_UNAVAILABLE','Voice is not configured. Presence remains available.',409);
  if(this.sessions.has(identity.deviceId))return;
  if(this.sessions.size>=32)throw new DomainError('VOICE_BUSY','Voice server is busy. Try again shortly.',429);
  const s:Session={identity,sink,closed:false,oneShot,followupMs:Math.max(0,Math.min(30,followupSeconds))*1000,generation:0,activeId:'',lastId:'',conversationId:'',lastActivity:Date.now(),started:Date.now(),turnFinishedAt:0,awaitingPlaybackId:'',outputStarted:false,playhead:0,wakeStopUntil:0,seen:new Set(),queue:Promise.resolve(),...(inputSampleRate===16000?{resampler:new Pcm16To24()}:{})};this.sessions.set(identity.deviceId,s);
  try{const c=await this.runtime.store.resolvePetConversation(identity.ownerId);s.conversationId=c.id;if(!this.current(s))return;this.runtime.events.emit('conversation_link',identity.ownerId);
   const connection=await this.provider.connect(e=>this.signal(s,e));if(s.closed){connection.close();return;}s.connection=connection;await sink.control({type:'voice_ready',sampleRate:24000,inputSampleRate,channels:1,format:'s16le'});
  }catch(e){await this.stopSession(s);throw e;}
 }
 active(deviceId:string){return this.sessions.has(deviceId);}
 announcing(deviceId:string){return this.announcements.get(deviceId)?.running??false;}
 outputActive(deviceId:string){const s=this.sessions.get(deviceId);return !!s&&(!!s.awaitingPlaybackId||!!s.activeId&&s.outputStarted);}
 setFollowupSeconds(deviceId:string,seconds:number){const s=this.sessions.get(deviceId);if(s)s.followupMs=seconds*1000;}
 private changed(deviceId:string){const waiters=this.playbackWaiters.get(deviceId);if(!waiters)return;this.playbackWaiters.delete(deviceId);for(const wake of waiters)wake();}
 private finishPlayback(s:Session,id:string){if(!this.current(s)||s.awaitingPlaybackId!==id)return;s.awaitingPlaybackId='';s.outputStarted=false;s.turnFinishedAt=Date.now();s.lastActivity=Date.now();this.changed(s.identity.deviceId);}
 playbackDone(deviceId:string,id:string){const s=this.sessions.get(deviceId);if(s)this.finishPlayback(s,id);}
 private async waitForOutput(deviceId:string){while(this.outputActive(deviceId))await new Promise<void>(resolve=>{const waiters=this.playbackWaiters.get(deviceId)??new Set<()=>void>();waiters.add(resolve);this.playbackWaiters.set(deviceId,waiters);if(!this.outputActive(deviceId)){waiters.delete(resolve);if(!waiters.size)this.playbackWaiters.delete(deviceId);resolve();}});}
 requestAnnouncement(identity:DeviceIdentity,sink:DeviceVoiceSink,text:string){
  if(!this.provider)throw new DomainError('VOICE_SPEECH_UNAVAILABLE','The configured voice service cannot speak a direct line.',409);
  const queued=this.outputActive(identity.deviceId)||this.announcing(identity.deviceId);const result=this.enqueueAnnouncement(identity,sink,text,true);
  return {accepted:true,state:queued?'queued' as const:'playing' as const,requestId:result.id,...(queued?{position:result.position}:{})};
 }
 async announce(identity:DeviceIdentity,sink:DeviceVoiceSink,text:string){
  if(!this.provider||this.sessions.has(identity.deviceId)||this.announcements.has(identity.deviceId))return false;
  return this.enqueueAnnouncement(identity,sink,text,false).completion;
 }
 private enqueueAnnouncement(identity:DeviceIdentity,sink:DeviceVoiceSink,text:string,preemptConversation:boolean){
  let queue=this.announcements.get(identity.deviceId);if(!queue){queue={items:[],running:false};this.announcements.set(identity.deviceId,queue);}
  if(queue.items.length>=8)throw new DomainError('VOICE_QUEUE_FULL','Marvin already has several lines queued. Try again after they play.',429);
  const id=randomUUID();let finish:(spoken:boolean)=>void=()=>{};const completion=new Promise<boolean>(resolve=>{finish=resolve;});const position=(queue.running?1:0)+queue.items.length+1;
  queue.items.push({id,identity,sink,text,preemptConversation,resolve:finish});if(!queue.running){queue.running=true;void this.drainAnnouncements(identity.deviceId,queue);}
  return {id,position,completion};
 }
 private async drainAnnouncements(deviceId:string,queue:AnnouncementQueue){
  let previous=false;
  try{while(this.announcements.get(deviceId)===queue){const job=queue.items.shift();if(!job)break;
    if(!this.provider){job.resolve(false);continue;}
    if(!job.preemptConversation&&this.sessions.has(deviceId)){job.resolve(false);continue;}
    const waitedForOutput=this.outputActive(deviceId);await this.waitForOutput(deviceId);if(this.announcements.get(deviceId)!==queue){job.resolve(false);break;}
    if(previous||waitedForOutput)await new Promise(resolve=>setTimeout(resolve,announcementGapMs));if(this.announcements.get(deviceId)!==queue){job.resolve(false);break;}
    if(job.preemptConversation)await this.stop(deviceId);
    else if(this.sessions.has(deviceId)){job.resolve(false);continue;}
    const controller=new AbortController();queue.controller=controller;const spoken=await this.playAnnouncement(job,controller);queue.controller=undefined;job.resolve(spoken);previous=true;
   }}finally{queue.controller?.abort();queue.controller=undefined;queue.running=false;if(this.announcements.get(deviceId)===queue){if(queue.items.length){queue.running=true;void this.drainAnnouncements(deviceId,queue);}else this.announcements.delete(deviceId);}}
 }
 private async playAnnouncement(job:AnnouncementJob,controller:AbortController){
  let connection:VoiceConnection|undefined;
  let playhead=performance.now();
  try{connection=await this.provider!.connect(()=>{});if(!connection.speak)return false;await job.sink.control({type:'voice_announcement',interactionId:job.id,sampleRate:24000,channels:1,format:'s16le'});
   for await(const pcm of connection.speak(job.text,controller.signal)){const bytes=Buffer.from(pcm);if(!bytes.length||bytes.length%2||bytes.length>12000)throw new Error('Invalid announcement audio');for(let at=0;at<bytes.length;at+=3840){const delay=playhead-performance.now()-playbackHeadroomMs;if(delay>0)await new Promise(resolve=>setTimeout(resolve,delay));controller.signal.throwIfAborted();const frame=bytes.subarray(at,at+3840);playhead=Math.max(playhead,performance.now())+frame.length/48;await job.sink.audio(job.id,frame);}}
   await job.sink.control({type:'voice_turn_end',interactionId:job.id});await new Promise(resolve=>setTimeout(resolve,750));controller.signal.throwIfAborted();await job.sink.control({type:'voice_closed'});return true;
  }catch{await job.sink.control({type:'audio_flush',interactionId:job.id}).catch(()=>{});await job.sink.control({type:'voice_closed'}).catch(()=>{});return false;}finally{controller.abort();connection?.close();}
 }
 append(deviceId:string,pcm:Buffer){const s=this.sessions.get(deviceId);if(!s||!s.connection||s.closed)throw new DomainError('AUDIO_NOT_ACTIVE','Start voice before uploading audio.',403);if(!pcm.length||pcm.length>4096||pcm.length%2)throw new DomainError('AUDIO_INVALID','Send mono s16le PCM in bounded frames.',400);s.connection.append(s.resampler?s.resampler.convert(pcm):pcm);}
 private current(s:Session){return !s.closed&&this.sessions.get(s.identity.deviceId)===s;}
 private async flush(s:Session){if(!this.current(s))return;const id=s.activeId||s.lastId;s.activeId='';s.awaitingPlaybackId='';s.outputStarted=false;s.turnFinishedAt=0;s.playhead=0;this.changed(s.identity.deviceId);await s.sink.control({type:'audio_flush',interactionId:id||null});if(id){await this.runtime.cancel(s.identity.ownerId,id);await this.runtime.waitTurn(id);}}
 async interrupt(deviceId:string,reason:'wake'|'manual'|'speech'='manual'){const s=this.sessions.get(deviceId);if(!s)return;s.generation++;if(reason==='wake')s.wakeStopUntil=Date.now()+10000;else if(reason==='manual')s.wakeStopUntil=0;await this.flush(s);}
 private signal(s:Session,e:VoiceSignal){if(!this.current(s))return;s.lastActivity=Date.now();
  if(e.type==='fault'){void this.stopSession(s);return;}
  if(this.announcing(s.identity.deviceId))return;
  if(e.type==='speech_start'){s.turnFinishedAt=0;s.awaitingPlaybackId='';void this.interrupt(s.identity.deviceId,'speech').catch(()=>this.stopSession(s));return;}
  if(e.type!=='transcript'||s.seen.has(e.itemId))return;s.seen.add(e.itemId);if(s.seen.size>500){void this.stopSession(s);return;}
  const generation=s.generation;s.turnFinishedAt=0;
  s.queue=s.queue.then(async()=>{const stale=()=>!this.current(s)||s.generation!==generation;if(stale()||!s.connection)return;const text=e.text.trim();const stop=s.wakeStopUntil>Date.now()&&isStopCommand(text);s.wakeStopUntil=0;if(stop){await this.stopSession(s);return;}await this.flush(s);if(stale())return;if(!text)return;if(text.length>12000)throw new Error('Spoken turn too long');
   const id=randomUUID();const context={...await this.runtime.context(s.identity.ownerId,s.conversationId,id,s.identity.deviceId),surface:'body_voice' as const};
   if(stale())return;
   if(context.body?.deviceId!==s.identity.deviceId||context.body.status!=='online')throw new Error('Device ownership or presence changed');
   s.activeId=s.lastId=id;s.outputStarted=false;await s.sink.control({type:'voice_turn',interactionId:id,text});if(stale())return;
   await this.runtime.start(context,text,{provider:s.connection.forTurn(e.itemId),audio:async pcm=>{if(s.closed||s.activeId!==id)return;const bytes=Buffer.from(pcm,'base64');if(bytes.length%2||bytes.length>480000)throw new Error('Invalid provider audio');for(let i=0;i<bytes.length;i+=3840){if(s.closed||s.activeId!==id)return;
    // Keep provider bursts within a small lead over physical playback. Awaiting
    // each frame bounds transport pressure and keeps interruption responsive.
    const delay=s.playhead-performance.now()-playbackHeadroomMs;if(delay>0)await new Promise(resolve=>setTimeout(resolve,delay));
    if(!this.current(s)||s.activeId!==id||s.generation!==generation)return;
    const frame=bytes.subarray(i,i+3840);s.outputStarted=true;s.playhead=Math.max(s.playhead,performance.now())+frame.length/48;
    await s.sink.audio(id,frame);}}});
   if(stale()){await this.runtime.cancel(s.identity.ownerId,id);await this.runtime.waitTurn(id);return;}
   // Keep lastId for playback flush even after generation finishes.
   await this.runtime.waitTurn(id);if(!s.closed&&s.activeId===id){s.activeId='';const playbackRemaining=Math.max(0,s.playhead-performance.now());s.playhead=0;s.awaitingPlaybackId=s.outputStarted?id:'';await s.sink.control({type:'voice_turn_end',interactionId:id});
    // Older firmware has no playback acknowledgement. Never cut off its queued audio.
    if(s.awaitingPlaybackId)setTimeout(()=>this.finishPlayback(s,id),Math.ceil(playbackRemaining)+500);else{s.outputStarted=false;s.turnFinishedAt=Date.now();this.changed(s.identity.deviceId);}}
  }).catch(()=>this.stopSession(s));
 }
 async stop(deviceId:string){const s=this.sessions.get(deviceId);if(s)await this.stopSession(s);}
 private async stopSession(s:Session){if(!this.current(s))return;const deviceId=s.identity.deviceId;s.closed=true;s.generation++;s.resampler?.clear();this.sessions.delete(deviceId);s.outputStarted=false;s.awaitingPlaybackId='';this.changed(deviceId);try{s.connection?.close();}catch{/* Continue local cleanup after provider transport failure. */}if(s.activeId)await this.runtime.cancel(s.identity.ownerId,s.activeId).catch(()=>{});await s.sink.control({type:'audio_flush',interactionId:s.activeId||s.lastId||null}).catch(()=>{});await s.sink.control({type:'voice_closed'}).catch(()=>{});}
 async disconnect(deviceId:string){const queue=this.announcements.get(deviceId);if(queue){this.announcements.delete(deviceId);queue.controller?.abort();for(const job of queue.items.splice(0))job.resolve(false);}this.changed(deviceId);await this.stop(deviceId);}
 async close(){clearInterval(this.timer);for(const id of [...this.announcements.keys()])await this.disconnect(id);await Promise.all([...this.sessions.keys()].map(id=>this.stop(id)));}
}
