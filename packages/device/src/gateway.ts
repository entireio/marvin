import type { DeviceVoice } from './voice.js';
import { EventEmitter } from 'node:events';
import type { FastifyInstance } from 'fastify';
import type WebSocket from 'ws';
import { Actions,DeviceControl,PetAudioSettings,type DeviceAction,type DeviceCapability,type DeviceCommand,type PetAudioSettings as AudioSettings } from '../../contracts/src/device.js';
import { DomainError,Id,type InteractionContext } from '../../contracts/src/index.js';
import { DeviceStore,type DeviceIdentity } from './store.js';

type Connection={identity:DeviceIdentity;token:string;bootId:string;protocolMinor:number;capabilities:DeviceCapability[];socket:WebSocket;lastSeen:number;lastSeq:number;candidate?:boolean;candidateSince:number;stablePickedUp:boolean;closed:boolean;openedAt:number;audioReceivedBytes:number;audioSentBytes:number;voiceStarts:number;audioInputRate:16000|24000;lateAudioUntil:number;audioSettings:AudioSettings|null};
export class DeviceGateway {
 voice?:DeviceVoice;
 readonly events=new EventEmitter();private online=new Map<string,Connection>();private greeting=new Set<string>();
 constructor(readonly persistence:DeviceStore){this.events.setMaxListeners(100);}
 presence(ownerId:string){const c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);return c?{deviceId:c.identity.deviceId,epoch:c.identity.epoch,bootId:c.bootId,capabilities:c.capabilities,status:'online' as const,audioSettings:c.audioSettings}:null;}
 diagnostics(ownerId:string){const c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);return c?{bootId:c.bootId,connectionOpenedAt:c.openedAt,audioReceivedBytes:c.audioReceivedBytes,audioSentBytes:c.audioSentBytes,voiceStarts:c.voiceStarts,voiceActive:this.voice?.active(c.identity.deviceId)??false}:null;}
 private async current(c:Connection){const id=await this.persistence.authenticate(c.token);if(c.closed||this.online.get(id.deviceId)!==c||id.epoch!==c.identity.epoch)throw new DomainError('DEVICE_OFFLINE','This device connection has ended.',409);return id;}
 private async send(c:Connection,event:unknown){await this.current(c);if(c.socket.readyState!==1||c.socket.bufferedAmount>65536){c.socket.close(1013,'Connection too slow');throw new DomainError('DEVICE_BACKPRESSURE','Your Desktop Pet needs to reconnect.',409);}c.socket.send(JSON.stringify(event));}
 async setAudioSettings(ownerId:string,value:unknown){
  const settings=PetAudioSettings.parse(value),c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to change its audio controls.',409);
  if(c.protocolMinor<4||!c.capabilities.includes('voice'))throw new DomainError('AUDIO_SETTINGS_UNAVAILABLE','Update your Desktop Pet firmware to control its audio.',409);
  if(settings.microphoneGainDb!==undefined&&c.protocolMinor<5)throw new DomainError('MICROPHONE_GAIN_UNAVAILABLE','Update your Desktop Pet firmware to control microphone sensitivity.',409);
  if((settings.allowPlaybackMic!==undefined||settings.followupSeconds!==undefined)&&c.protocolMinor<6)throw new DomainError('FOLLOWUP_UNAVAILABLE','Update your Desktop Pet firmware to configure conversation audio.',409);
  const next=c.protocolMinor>=6?{...settings,microphoneGainDb:settings.microphoneGainDb??c.audioSettings?.microphoneGainDb,allowPlaybackMic:settings.allowPlaybackMic??c.audioSettings?.allowPlaybackMic,followupSeconds:settings.followupSeconds??c.audioSettings?.followupSeconds}:c.protocolMinor>=5?{...settings,microphoneGainDb:settings.microphoneGainDb??c.audioSettings?.microphoneGainDb}:settings;
  if(c.protocolMinor>=5&&next.microphoneGainDb===undefined)throw new DomainError('AUDIO_SETTINGS_UNAVAILABLE','Reconnect your Desktop Pet to read its microphone sensitivity.',409);
  if(c.protocolMinor>=6&&(next.allowPlaybackMic===undefined||next.followupSeconds===undefined))throw new DomainError('AUDIO_SETTINGS_UNAVAILABLE','Reconnect your Desktop Pet to read its conversation settings.',409);
  await this.send(c,{type:'audio_settings',...next});c.audioSettings=next;if(next.followupSeconds!==undefined)this.voice?.setFollowupSeconds(c.identity.deviceId,next.followupSeconds);this.events.emit('audio_settings',c.identity,next);return next;
 }
 private sink(c:Connection){return {control:async(event:unknown)=>{if((event as {type?:string})?.type==='voice_closed')c.lateAudioUntil=Date.now()+1000;await this.send(c,event);},audio:async(id:string,pcm:Buffer)=>{await this.current(c);if(c.socket.readyState!==1||c.socket.bufferedAmount>65536)throw new Error('Audio backpressure');const header=Buffer.concat([Buffer.from('MVA1'),Buffer.from(id.replaceAll('-',''),'hex')]);c.socket.send(Buffer.concat([header,pcm]));c.audioSentBytes+=pcm.length;}};}
 private async greet(c:Connection){const id=c.identity.deviceId;if(this.greeting.has(id)||c.protocolMinor<3||!c.capabilities.includes('voice')||!this.voice||!await this.persistence.greetingPending(c.identity))return;this.greeting.add(id);try{if(await this.voice.announce(c.identity,this.sink(c),"Your Desktop Pet is linked. I'm Marvin. Apparently we're in this together now."))await this.persistence.markGreeted(c.identity);}finally{this.greeting.delete(id);}}
 async dispatch(ctx:InteractionContext,action:DeviceAction,args:unknown,id:string,ttlMs=3000){
  if(ctx.surface!=='body_voice'||!ctx.body||ctx.routeId!==ctx.body.deviceId)throw new DomainError('PHYSICAL_ROUTE_FORBIDDEN','Physical actions require a request made through the originating Desktop Pet.',403);
  Id.parse(id);const parsed=Actions[action]?.parse(args);if(!parsed)throw new DomainError('ACTION_UNSUPPORTED','Unknown physical action.',400);
  const c=this.online.get(ctx.body.deviceId);if(!c||c.identity.ownerId!==ctx.ownerId)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet is offline.',409);
  if(!c.capabilities.includes(action))throw new DomainError('CAPABILITY_UNAVAILABLE','This Desktop Pet does not support that action.',409);
  if((action==='tracks'||action==='motion')&&((!c.capabilities.includes('cliff')&&!c.capabilities.includes('bench_tracks'))||c.stablePickedUp))throw new DomainError('MOTION_UNSAFE','Track motion requires cliff sensing or a locally armed bench test, and is disabled while picked up.',409);
  const identity=await this.current(c);if(ttlMs<1||ttlMs>5000)throw new DomainError('DEADLINE_INVALID','Action deadlines must be within five seconds.',400);
  const command:DeviceCommand={type:'command',id,deviceId:identity.deviceId,epoch:identity.epoch,bootId:c.bootId,action,args:parsed,deadline:Date.now()+ttlMs};
  const result=await this.persistence.create(identity,command);if(!result.created)return {id,state:result.state,accepted:result.state==='accepted'||result.state==='completed'};
  let finish:(status:{status:string;code?:string}|null)=>void=()=>{};
  const acknowledged=new Promise<{status:string;code?:string}|null>(resolve=>{finish=resolve;});
  const listener=(source:DeviceIdentity,event:{id:string;status:string;code?:string})=>{if(source.deviceId===identity.deviceId&&event.id===id&&event.status!=='sent')finish(event);};
  this.events.on('command',listener);
  const timeout=setTimeout(()=>finish(null),600);
  try{await this.send(c,command);const reply=await acknowledged;return {id,state:reply?.status??'sent',accepted:reply?.status==='accepted'||reply?.status==='completed',...(reply?.code?{code:reply.code}:{})};}
  finally{clearTimeout(timeout);this.events.off('command',listener);}
 }
 async cancel(ownerId:string,deviceId:string,id:string){const c=this.online.get(deviceId);if(!c||c.identity.ownerId!==ownerId)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet is offline.',409);const identity=await this.current(c);if(await this.persistence.cancel(identity,id))await this.send(c,{type:'cancel',id,bootId:c.bootId,epoch:identity.epoch});}
 revoke(ownerId:string){for(const c of this.online.values())if(c.identity.ownerId===ownerId){c.closed=true;this.online.delete(c.identity.deviceId);c.socket.close(4401,'Device access revoked');void this.voice?.stop(c.identity.deviceId);this.events.emit('offline',c.identity);}}
 close(){for(const c of this.online.values())c.socket.close(1001,'Server shutting down');this.online.clear();}
 register(app:FastifyInstance){app.get('/api/device/socket',{websocket:true},(socket,req)=>{
  if(req.headers.origin){socket.close(4403,'Use the native device transport');return;}
  const token=req.headers.authorization?.replace(/^Bearer /,'')??'';let c:Connection|undefined,closed=false,queue=Promise.resolve(),pending=0,window=Date.now(),count=0,bytes=0,initialized=false;
  const close=(code:number,message:string)=>{closed=true;socket.close(code,message);};
  const setup=setTimeout(()=>{if(!c)close(4408,'Send hello after connecting');},5000);
  const heartbeat=setInterval(()=>{if(c){if(Date.now()-c.lastSeen>15000){close(4408,'Heartbeat expired');return;}void this.current(c).then(()=>this.send(c!,{type:'ping'})).catch(()=>close(4401,'Device access revoked'));}},5000);
  socket.on('message',(raw,binary)=>{
   if(closed)return;if(Date.now()-window>=1000){window=Date.now();count=0;bytes=0;}bytes+=Buffer.byteLength(raw as Buffer);if(bytes>100000||++count>100||++pending>128){close(4429,'Device message rate exceeded');return;}
   queue=queue.then(async()=>{
    if(closed)return;
    if(binary){if(!c)throw new DomainError('HELLO_REQUIRED','Send hello first.',400);await this.current(c);if(!this.voice?.active(c.identity.deviceId)){if(Date.now()<=c.lateAudioUntil)return;throw new DomainError('AUDIO_NOT_ACTIVE','Idle devices cannot upload audio.',403);}c.audioReceivedBytes+=Buffer.byteLength(raw as Buffer);this.voice.append(c.identity.deviceId,Buffer.from(raw as Buffer));return;}
    const message=DeviceControl.parse(JSON.parse(raw.toString()));
    if(message.type==='hello'){
     if(initialized)throw new DomainError('HELLO_DUPLICATE','Hello was already received.',400);initialized=true;
     if(message.protocol.major!==1||![0,1,2,3,4,5,6].includes(message.protocol.minor)){socket.send(JSON.stringify({type:'error',code:'UPGRADE_REQUIRED',message:'Use supported protocol 1.0–1.6 firmware.'}));close(4406,'Firmware protocol upgrade required');return;}
     if(message.audioInputRate&&message.protocol.minor<2)throw new DomainError('AUDIO_PROTOCOL','Native input rate requires protocol 1.2.',400);
     if(message.audioSettings&&message.protocol.minor<4)throw new DomainError('AUDIO_PROTOCOL','Audio settings require protocol 1.4.',400);
     if(message.audioSettings?.microphoneGainDb!==undefined&&message.protocol.minor<5)throw new DomainError('AUDIO_PROTOCOL','Microphone gain requires protocol 1.5.',400);
     if((message.audioSettings?.allowPlaybackMic!==undefined||message.audioSettings?.followupSeconds!==undefined)&&message.protocol.minor<6)throw new DomainError('AUDIO_PROTOCOL','Conversation audio controls require protocol 1.6.',400);
     if(message.protocol.minor>=6&&message.capabilities.includes('voice')&&(message.audioSettings?.allowPlaybackMic===undefined||message.audioSettings?.followupSeconds===undefined))throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings are missing.',400);
     const identity=await this.persistence.authenticate(token);if(identity.deviceId!==message.deviceId)throw new DomainError('DEVICE_MISMATCH','Device identity mismatch.',403);
     if(this.online.size>=1000&&!this.online.has(identity.deviceId))throw new DomainError('GATEWAY_BUSY','Device gateway is busy.',429);
     const previous=this.online.get(identity.deviceId);if(previous){await this.voice?.stop(identity.deviceId);previous.closed=true;previous.socket.close(4409,'Replaced by a new device connection');}
     c={identity,token,bootId:message.bootId,protocolMinor:message.protocol.minor,capabilities:[...new Set(message.capabilities)],socket,lastSeen:Date.now(),lastSeq:-1,candidateSince:0,stablePickedUp:false,closed:false,openedAt:Date.now(),audioReceivedBytes:0,audioSentBytes:0,voiceStarts:0,audioInputRate:message.audioInputRate??24000,lateAudioUntil:0,audioSettings:message.audioSettings??null};this.online.set(identity.deviceId,c);clearTimeout(setup);
     await this.send(c,{type:'welcome',protocol:{major:1,minor:message.protocol.minor},epoch:identity.epoch,heartbeatMs:5000,serverTime:Date.now()});
     for(const command of await this.persistence.reconnect(identity,c.bootId))await this.send(c,command);this.events.emit('online',identity);void this.greet(c).catch(()=>{});return;
    }
    if(!c)throw new DomainError('HELLO_REQUIRED','Send hello first.',400);await this.current(c);c.lastSeen=Date.now();
    if(message.type==='heartbeat'){if(message.seq<=c.lastSeq)throw new DomainError('STALE_FRAME','Heartbeat sequence must increase.',400);c.lastSeq=message.seq;return;}
    if(message.type==='audio_settings'){
     if(c.protocolMinor<4||!c.capabilities.includes('voice'))throw new DomainError('CAPABILITY_UNAVAILABLE','Audio settings were not negotiated.',400);
     if(message.microphoneGainDb!==undefined&&c.protocolMinor<5)throw new DomainError('AUDIO_PROTOCOL','Microphone gain was not negotiated.',400);
     if((message.allowPlaybackMic!==undefined||message.followupSeconds!==undefined)&&c.protocolMinor<6)throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings were not negotiated.',400);
     if(c.protocolMinor>=6&&(message.allowPlaybackMic===undefined||message.followupSeconds===undefined))throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings are missing.',400);
     c.audioSettings={volume:message.volume,muted:message.muted,...(message.microphoneGainDb!==undefined?{microphoneGainDb:message.microphoneGainDb}:{}),...(message.allowPlaybackMic!==undefined?{allowPlaybackMic:message.allowPlaybackMic}:{}),...(message.followupSeconds!==undefined?{followupSeconds:message.followupSeconds}:{})};this.voice?.setFollowupSeconds(c.identity.deviceId,c.audioSettings.followupSeconds??0);this.events.emit('audio_settings',c.identity,c.audioSettings);return;
    }
    if(message.type==='command_status'){await this.persistence.acknowledge(c.identity,c.bootId,message.id,message.status);this.events.emit('command',c.identity,{id:message.id,status:message.status,code:message.code});return;}
    if(message.type==='sensor'){
     if(!c.capabilities.includes('imu'))throw new DomainError('CAPABILITY_UNAVAILABLE','No IMU was negotiated.',400);
     if(c.candidate!==message.pickedUp){c.candidate=message.pickedUp;c.candidateSince=Date.now();}
     if(c.stablePickedUp!==message.pickedUp&&Date.now()-c.candidateSince>=150){c.stablePickedUp=message.pickedUp;this.events.emit('sensor',c.identity,{type:message.pickedUp?'picked_up':'put_down'});}return;
    }
    if(message.type==='voice_stop'){await this.voice?.stop(c.identity.deviceId);return;}
    if(message.type==='voice_playback_done'){if(c.protocolMinor<6)throw new DomainError('AUDIO_PROTOCOL','Playback acknowledgement was not negotiated.',400);this.voice?.playbackDone(c.identity.deviceId,message.interactionId);return;}
    if(message.type==='voice_interrupt'){await this.voice?.interrupt(c.identity.deviceId);return;}
    if(message.type==='voice_start'){
     if(!c.capabilities.includes('voice'))throw new DomainError('CAPABILITY_UNAVAILABLE','Voice hardware was not negotiated.',409);
     const current=c;
     try{if(!this.voice)throw new DomainError('VOICE_UNAVAILABLE','Voice is not configured.',409);
      const alreadyActive=this.voice.active(c.identity.deviceId);
      current.lateAudioUntil=0;
      await this.voice.start(c.identity,this.sink(current),c.audioInputRate,message.reason==='wake',c.audioSettings?.followupSeconds??0);
      if(!alreadyActive&&this.voice.active(c.identity.deviceId))current.voiceStarts++;
     }catch{await this.send(current,{type:'voice_error',message:'Voice could not connect. Try again later; your Desktop Pet remains online.'});}return;
    }
    throw new DomainError('UNSUPPORTED_MESSAGE','Unsupported device message.',400);
   }).catch(e=>{if(!closed){socket.send(JSON.stringify({type:'error',code:e instanceof DomainError?e.code:'INVALID_DEVICE_MESSAGE',message:e instanceof DomainError?e.message:'Invalid device message.'}));close(4400,'Device request rejected');}}).finally(()=>{pending--;});
  });
  socket.on('error',()=>close(1011,'Device connection failed'));
  socket.on('close',()=>{closed=true;clearTimeout(setup);clearInterval(heartbeat);if(c&&this.online.get(c.identity.deviceId)===c){c.closed=true;this.online.delete(c.identity.deviceId);void this.voice?.stop(c.identity.deviceId);this.events.emit('offline',c.identity);}});
 });}
}
