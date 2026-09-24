import type { DeviceVoice } from './voice.js';
import { EventEmitter } from 'node:events';
import type { FastifyInstance } from 'fastify';
import type WebSocket from 'ws';
import { Actions,BatteryStatus,DeviceControl,HeadCalibration,PetAudioSettings,PetDisplaySettings,PetEyeSettings,type BatteryStatus as BatteryState,type DeviceAction,type DeviceCapability,type DeviceCommand,type HeadCalibration as HeadCalibrationSettings,type PetAudioSettings as AudioSettings,type PetDisplaySettings as DisplaySettings,type PetEyeSettings as EyeSettings } from '../../contracts/src/device.js';
import { DomainError,Id,type InteractionContext } from '../../contracts/src/index.js';
import { DeviceStore,type DeviceIdentity } from './store.js';
import { decodeAdpcmFrame } from './adpcm.js';

type LinkDiagnostics={wifiRssi:number;audioTimeouts:number;internalFreeBytes:number;internalLargestBlock:number;resetReason:number;lastLinkFault:number};
type Connection={identity:DeviceIdentity;token:string;bootId:string;protocolMinor:number;capabilities:DeviceCapability[];socket:WebSocket;lastSeen:number;lastSeq:number;candidate?:boolean;candidateSince:number;stablePickedUp:boolean;closed:boolean;openedAt:number;audioReceivedBytes:number;audioDecodedBytes:number;audioTimeouts:number;audioSentBytes:number;voiceStarts:number;audioInputRate:16000|24000;audioCodec?:'ima-adpcm';lateAudioUntil:number;audioSettings:AudioSettings|null;headCalibration:HeadCalibrationSettings|null;batteryStatus:BatteryState|null;eyeSettings:EyeSettings|null;displaySettings:DisplaySettings|null;linkDiagnostics:LinkDiagnostics|null;remoteIds:Set<string>;remoteOrder:string[]};
type Recent={connection:Connection;expires:number};
export class DeviceGateway {
 voice?:DeviceVoice;
 readonly events=new EventEmitter();private online=new Map<string,Connection>();private recent=new Map<string,Recent>();private greeting=new Set<string>();
 constructor(readonly persistence:DeviceStore){this.events.setMaxListeners(100);}
 presence(ownerId:string){const c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);return c?{deviceId:c.identity.deviceId,epoch:c.identity.epoch,bootId:c.bootId,capabilities:c.capabilities,status:'online' as const,audioSettings:c.audioSettings,headCalibration:c.headCalibration,headCalibrationPreview:c.protocolMinor>=11,batteryStatus:c.batteryStatus,eyeSettings:c.eyeSettings,displaySettings:c.displaySettings}:null;}
 publicPresence(ownerId:string){const live=this.presence(ownerId);if(live)return live;const r=[...this.recent.values()].find(r=>r.connection.identity.ownerId===ownerId&&r.expires>Date.now()),c=r?.connection;return c?{deviceId:c.identity.deviceId,epoch:c.identity.epoch,bootId:c.bootId,capabilities:c.capabilities,status:'reconnecting' as const,audioSettings:c.audioSettings,headCalibration:c.headCalibration,headCalibrationPreview:c.protocolMinor>=11,batteryStatus:c.batteryStatus,eyeSettings:c.eyeSettings,displaySettings:c.displaySettings}:null;}
 diagnostics(ownerId:string){const online=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed),r=[...this.recent.values()].find(r=>r.connection.identity.ownerId===ownerId&&r.expires>Date.now()),c=online??r?.connection;return c?{status:online?'online':'reconnecting',bootId:c.bootId,connectionOpenedAt:c.openedAt,audioReceivedBytes:c.audioReceivedBytes,audioDecodedBytes:c.audioDecodedBytes,audioTimeouts:c.audioTimeouts,audioSentBytes:c.audioSentBytes,voiceStarts:c.voiceStarts,voiceActive:this.voice?.active(c.identity.deviceId)??false,link:c.linkDiagnostics}:null;}
 private async current(c:Connection){const id=await this.persistence.authenticate(c.token);if(c.closed||this.online.get(id.deviceId)!==c||id.epoch!==c.identity.epoch)throw new DomainError('DEVICE_OFFLINE','This device connection has ended.',409);return id;}
 private sendConnected(c:Connection,event:unknown){if(c.closed||this.online.get(c.identity.deviceId)!==c)throw new DomainError('DEVICE_OFFLINE','This device connection has ended.',409);if(c.socket.readyState!==1||c.socket.bufferedAmount>65536){c.socket.close(1013,'Connection too slow');throw new DomainError('DEVICE_BACKPRESSURE','Your Desktop Pet needs to reconnect.',409);}c.socket.send(JSON.stringify(event));}
 private async send(c:Connection,event:unknown){await this.current(c);this.sendConnected(c,event);}
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
 async setHeadCalibration(ownerId:string,value:unknown){
  const calibration=HeadCalibration.parse(value),c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to calibrate its head.',409);
  if(c.protocolMinor<8||!c.capabilities.includes('head'))throw new DomainError('HEAD_CALIBRATION_UNAVAILABLE','Update your Desktop Pet firmware to calibrate its head.',409);
  let finish:(settings:HeadCalibrationSettings|null)=>void=()=>{};
  const confirmed=new Promise<HeadCalibrationSettings|null>(resolve=>{finish=resolve;});
  const listener=(source:DeviceIdentity,settings:HeadCalibrationSettings)=>{if(source.deviceId===c.identity.deviceId&&settings.yawCenter===calibration.yawCenter&&settings.pitchCenter===calibration.pitchCenter&&settings.yawReversed===calibration.yawReversed&&settings.pitchReversed===calibration.pitchReversed)finish(settings);};
  this.events.on('head_calibration',listener);const timeout=setTimeout(()=>finish(null),1200);
  try{await this.send(c,{type:'head_calibration',...calibration});const saved=await confirmed;if(!saved)throw new DomainError('HEAD_CALIBRATION_TIMEOUT','Your Desktop Pet did not confirm the calibration. Reconnect it and try again.',409);return saved;}
  finally{clearTimeout(timeout);this.events.off('head_calibration',listener);}
 }
 previewHeadCalibration(ownerId:string,value:unknown){
  const calibration=HeadCalibration.parse(value),c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to preview its head calibration.',409);
  if(c.protocolMinor<11||!c.capabilities.includes('head'))throw new DomainError('HEAD_CALIBRATION_PREVIEW_UNAVAILABLE','Update your Desktop Pet firmware to preview head calibration.',409);
  /* Preview samples are transient and rate-bounded by the web client. They
     use the already authenticated live connection and never enter durable
     command storage or wait for a flash-backed acknowledgement. */
  this.sendConnected(c,{type:'head_calibration_preview',...calibration});
  return {accepted:true};
 }
 async setEyeSettings(ownerId:string,value:unknown){
  const settings=PetEyeSettings.parse(value),c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to change its eye design.',409);
  if(c.protocolMinor<10||!c.capabilities.includes('eyes'))throw new DomainError('EYE_SETTINGS_UNAVAILABLE','Update your Desktop Pet firmware to choose an eye design.',409);
  let finish:(saved:EyeSettings|null)=>void=()=>{};
  const confirmed=new Promise<EyeSettings|null>(resolve=>{finish=resolve;});
  const listener=(source:DeviceIdentity,saved:EyeSettings)=>{if(source.deviceId===c.identity.deviceId&&saved.design===settings.design)finish(saved);};
  this.events.on('eye_settings',listener);const timeout=setTimeout(()=>finish(null),1200);
  try{await this.send(c,{type:'eye_settings',...settings});const saved=await confirmed;if(!saved)throw new DomainError('EYE_SETTINGS_TIMEOUT','Your Desktop Pet did not confirm the eye design. Reconnect it and try again.',409);return saved;}
  finally{clearTimeout(timeout);this.events.off('eye_settings',listener);}
 }
 async setDisplaySettings(ownerId:string,value:unknown){
  const settings=PetDisplaySettings.parse(value),c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to change its display settings.',409);
  if(c.protocolMinor<13||!c.displaySettings)throw new DomainError('DISPLAY_SETTINGS_UNAVAILABLE','Update your Desktop Pet firmware to change its battery indicator.',409);
  let finish:(saved:DisplaySettings|null)=>void=()=>{};
  const confirmed=new Promise<DisplaySettings|null>(resolve=>{finish=resolve;});
  const listener=(source:DeviceIdentity,saved:DisplaySettings)=>{if(source.deviceId===c.identity.deviceId&&saved.showBatteryIcon===settings.showBatteryIcon)finish(saved);};
  this.events.on('display_settings',listener);const timeout=setTimeout(()=>finish(null),1200);
  try{await this.send(c,{type:'display_settings',...settings});const saved=await confirmed;if(!saved)throw new DomainError('DISPLAY_SETTINGS_TIMEOUT','Your Desktop Pet did not confirm the battery indicator setting. Reconnect it and try again.',409);return saved;}
  finally{clearTimeout(timeout);this.events.off('display_settings',listener);}
 }
 async speak(ownerId:string,text:string){
  const c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to speak.',409);
  if(!c.capabilities.includes('voice'))throw new DomainError('CAPABILITY_UNAVAILABLE','This Desktop Pet does not support speech.',409);
  if(!this.voice)throw new DomainError('VOICE_UNAVAILABLE','Voice is not configured on this server.',409);
  return this.voice.requestAnnouncement(c.identity,this.sink(c),text);
 }
 private sink(c:Connection){return {control:async(event:unknown)=>{if((event as {type?:string})?.type==='voice_closed')c.lateAudioUntil=Date.now()+1000;await this.send(c,event);},audio:async(id:string,pcm:Buffer)=>{await this.current(c);if(c.socket.readyState!==1||c.socket.bufferedAmount>65536)throw new Error('Audio backpressure');const header=Buffer.concat([Buffer.from('MVA1'),Buffer.from(id.replaceAll('-',''),'hex')]);c.socket.send(Buffer.concat([header,pcm]));c.audioSentBytes+=pcm.length;}};}
 private async greet(c:Connection){const id=c.identity.deviceId;if(this.greeting.has(id)||c.protocolMinor<3||!c.capabilities.includes('voice')||!this.voice||!await this.persistence.greetingPending(c.identity))return;this.greeting.add(id);try{if(await this.voice.announce(c.identity,this.sink(c),"Your Desktop Pet is linked. I'm Marvin. Apparently we're in this together now."))await this.persistence.markGreeted(c.identity);}finally{this.greeting.delete(id);}}
 private supports(c:Connection,action:DeviceAction){return c.capabilities.includes(action);}
 private async dispatchConnection(c:Connection,action:DeviceAction,args:unknown,id:string,ttlMs:number,remote=false){
  Id.parse(id);const parsed=Actions[action]?.parse(args);if(!parsed)throw new DomainError('ACTION_UNSUPPORTED','Unknown physical action.',400);
  if(!this.supports(c,action))throw new DomainError('CAPABILITY_UNAVAILABLE','This Desktop Pet does not support that action.',409);
  if((action==='tracks'||action==='motion'||action==='remote')&&c.stablePickedUp)throw new DomainError('MOTION_UNSAFE','Track motion is disabled while Marvin is picked up.',409);
  if(ttlMs<1||ttlMs>5000)throw new DomainError('DEADLINE_INVALID','Action deadlines must be within five seconds.',400);
  /* The device socket was authenticated when it connected and is revalidated
     by the five-second gateway heartbeat. Remote intent must stay on this
     in-memory path: authenticating every sample adds two database round trips
     and makes a 20 Hz joystick depend on Cloud SQL latency. Account unlinking
     still calls revoke(), and socket loss still stops the firmware locally. */
  if(action==='remote'){
   const command:DeviceCommand={type:'command',id,deviceId:c.identity.deviceId,epoch:c.identity.epoch,bootId:c.bootId,action,args:parsed,deadline:Date.now()+ttlMs};
   c.remoteIds.add(id);c.remoteOrder.push(id);while(c.remoteOrder.length>64)c.remoteIds.delete(c.remoteOrder.shift()!);
   try{this.sendConnected(c,command);}catch(error){c.remoteIds.delete(id);throw error;}
   return {id,state:'accepted',accepted:true};
  }
  const identity=await this.current(c);
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
 async dispatch(ctx:InteractionContext,action:DeviceAction,args:unknown,id:string,ttlMs=3000){
  if(ctx.surface!=='body_voice'||!ctx.body||ctx.routeId!==ctx.body.deviceId)throw new DomainError('PHYSICAL_ROUTE_FORBIDDEN','Physical actions require a request made through the originating Desktop Pet.',403);
  const c=this.online.get(ctx.body.deviceId);if(!c||c.identity.ownerId!==ctx.ownerId)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet is offline.',409);
  return this.dispatchConnection(c,action,args,id,ttlMs);
 }
 async dispatchRemote(ownerId:string,action:'head'|'tracks'|'remote',args:unknown,id:string,ttlMs:number){
  const c=[...this.online.values()].find(c=>c.identity.ownerId===ownerId&&!c.closed);
  if(!c)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet must be online to use its remote control.',409);
  return this.dispatchConnection(c,action,args,id,ttlMs,true);
 }
 async cancel(ownerId:string,deviceId:string,id:string){const c=this.online.get(deviceId);if(!c||c.identity.ownerId!==ownerId)throw new DomainError('DEVICE_OFFLINE','Your Desktop Pet is offline.',409);const identity=await this.current(c);if(await this.persistence.cancel(identity,id))await this.send(c,{type:'cancel',id,bootId:c.bootId,epoch:identity.epoch});}
 revoke(ownerId:string){for(const [id,r] of this.recent)if(r.connection.identity.ownerId===ownerId)this.recent.delete(id);for(const c of this.online.values())if(c.identity.ownerId===ownerId){c.closed=true;this.online.delete(c.identity.deviceId);c.socket.close(4401,'Device access revoked');void this.voice?.disconnect(c.identity.deviceId);this.events.emit('offline',c.identity);}}
 close(){for(const c of this.online.values()){c.socket.close(1001,'Server shutting down');void this.voice?.disconnect(c.identity.deviceId);}this.online.clear();this.recent.clear();}
 register(app:FastifyInstance){app.get('/api/device/socket',{websocket:true},(socket,req)=>{
  if(req.headers.origin){socket.close(4403,'Use the native device transport');return;}
  const token=req.headers.authorization?.replace(/^Bearer /,'')??'';let c:Connection|undefined,closed=false,queue=Promise.resolve(),pending=0,window=Date.now(),count=0,bytes=0,initialized=false;
  const close=(code:number,message:string)=>{closed=true;socket.close(code,message);};
  const setup=setTimeout(()=>{if(!c)close(4408,'Send hello after connecting');},5000);
  const heartbeat=setInterval(()=>{if(c){if(Date.now()-c.lastSeen>45000){close(4408,'Heartbeat expired');return;}void this.current(c).then(()=>this.send(c!,{type:'ping'})).catch(()=>close(4401,'Device access revoked'));}},5000);
  socket.on('message',(raw,binary)=>{
   if(closed)return;if(Date.now()-window>=1000){window=Date.now();count=0;bytes=0;}bytes+=Buffer.byteLength(raw as Buffer);if(bytes>100000||++count>100||++pending>128){close(4429,'Device message rate exceeded');return;}
   queue=queue.then(async()=>{
    if(closed)return;
    if(binary){if(!c)throw new DomainError('HELLO_REQUIRED','Send hello first.',400);await this.current(c);if(!this.voice?.active(c.identity.deviceId)){if(Date.now()<=c.lateAudioUntil)return;throw new DomainError('AUDIO_NOT_ACTIVE','Idle devices cannot upload audio.',403);}const encoded=Buffer.from(raw as Buffer),pcm=c.protocolMinor>=12?decodeAdpcmFrame(encoded).pcm:encoded;c.audioReceivedBytes+=encoded.length;c.audioDecodedBytes+=pcm.length;this.voice.append(c.identity.deviceId,pcm);return;}
    const message=DeviceControl.parse(JSON.parse(raw.toString()));
    if(message.type==='hello'){
     if(initialized)throw new DomainError('HELLO_DUPLICATE','Hello was already received.',400);initialized=true;
     if(message.protocol.major!==1||message.protocol.minor<0||message.protocol.minor>13){socket.send(JSON.stringify({type:'error',code:'UPGRADE_REQUIRED',message:'Use supported protocol 1.0–1.13 firmware.'}));close(4406,'Firmware protocol upgrade required');return;}
     if(message.audioInputRate&&message.protocol.minor<2)throw new DomainError('AUDIO_PROTOCOL','Native input rate requires protocol 1.2.',400);
     if(message.audioSettings&&message.protocol.minor<4)throw new DomainError('AUDIO_PROTOCOL','Audio settings require protocol 1.4.',400);
     if(message.audioSettings?.microphoneGainDb!==undefined&&message.protocol.minor<5)throw new DomainError('AUDIO_PROTOCOL','Microphone gain requires protocol 1.5.',400);
     if((message.audioSettings?.allowPlaybackMic!==undefined||message.audioSettings?.followupSeconds!==undefined)&&message.protocol.minor<6)throw new DomainError('AUDIO_PROTOCOL','Conversation audio controls require protocol 1.6.',400);
     if(message.protocol.minor>=6&&message.capabilities.includes('voice')&&(message.audioSettings?.allowPlaybackMic===undefined||message.audioSettings?.followupSeconds===undefined))throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings are missing.',400);
     if(message.headCalibration&&message.protocol.minor<8)throw new DomainError('HEAD_PROTOCOL','Head calibration requires protocol 1.8.',400);
     if(message.protocol.minor>=8&&message.capabilities.includes('head')&&!message.headCalibration)throw new DomainError('HEAD_PROTOCOL','Head calibration is missing.',400);
     if(message.batteryStatus&&message.protocol.minor<9)throw new DomainError('BATTERY_PROTOCOL','Battery status requires protocol 1.9.',400);
     if(message.protocol.minor>=9&&!message.batteryStatus)throw new DomainError('BATTERY_PROTOCOL','Battery status is missing.',400);
     if(message.eyeSettings&&message.protocol.minor<10)throw new DomainError('EYE_PROTOCOL','Eye settings require protocol 1.10.',400);
     if(message.protocol.minor>=10&&message.capabilities.includes('eyes')&&!message.eyeSettings)throw new DomainError('EYE_PROTOCOL','Eye settings are missing.',400);
     if(message.displaySettings&&message.protocol.minor<13)throw new DomainError('DISPLAY_PROTOCOL','Display settings require protocol 1.13.',400);
     if(message.protocol.minor>=13&&!message.displaySettings)throw new DomainError('DISPLAY_PROTOCOL','Display settings are missing.',400);
     if(message.protocol.minor>=12&&message.capabilities.includes('voice')&&(message.audioInputRate!==16000||message.audioCodec!=='ima-adpcm'))throw new DomainError('AUDIO_PROTOCOL','Protocol 1.12 voice requires negotiated compressed audio.',400);
     if(message.protocol.minor<12&&message.audioCodec)throw new DomainError('AUDIO_PROTOCOL','Compressed audio requires protocol 1.12.',400);
     const identity=await this.persistence.authenticate(token);if(identity.deviceId!==message.deviceId)throw new DomainError('DEVICE_MISMATCH','Device identity mismatch.',403);
     if(this.online.size>=1000&&!this.online.has(identity.deviceId))throw new DomainError('GATEWAY_BUSY','Device gateway is busy.',429);
     const previous=this.online.get(identity.deviceId);if(previous){await this.voice?.disconnect(identity.deviceId);previous.closed=true;previous.socket.close(4409,'Replaced by a new device connection');}
     c={identity,token,bootId:message.bootId,protocolMinor:message.protocol.minor,capabilities:[...new Set(message.capabilities)],socket,lastSeen:Date.now(),lastSeq:-1,candidateSince:0,stablePickedUp:false,closed:false,openedAt:Date.now(),audioReceivedBytes:0,audioDecodedBytes:0,audioTimeouts:0,audioSentBytes:0,voiceStarts:0,audioInputRate:message.audioInputRate??24000,audioCodec:message.audioCodec,lateAudioUntil:0,audioSettings:message.audioSettings??null,headCalibration:message.headCalibration??null,batteryStatus:message.batteryStatus??null,eyeSettings:message.eyeSettings??null,displaySettings:message.displaySettings??null,linkDiagnostics:null,remoteIds:new Set(),remoteOrder:[]};this.online.set(identity.deviceId,c);this.recent.delete(identity.deviceId);clearTimeout(setup);
     await this.send(c,{type:'welcome',protocol:{major:1,minor:message.protocol.minor},epoch:identity.epoch,heartbeatMs:5000,serverTime:Date.now()});
     for(const command of await this.persistence.reconnect(identity,c.bootId))await this.send(c,command);this.events.emit('online',identity);void this.greet(c).catch(()=>{});return;
    }
    if(!c)throw new DomainError('HELLO_REQUIRED','Send hello first.',400);
    /* Older firmware acknowledges every remote sample. Recognize those
       ephemeral IDs before the durable-command path so mixed-version fleets
       also avoid a credential query and no-op database update per sample. */
    if(message.type==='command_status'&&c.remoteIds.delete(message.id)){c.lastSeen=Date.now();return;}
    await this.current(c);c.lastSeen=Date.now();
    if(message.type==='heartbeat'){if(message.seq<=c.lastSeq)throw new DomainError('STALE_FRAME','Heartbeat sequence must increase.',400);c.lastSeq=message.seq;return;}
    if(message.type==='audio_settings'){
     if(c.protocolMinor<4||!c.capabilities.includes('voice'))throw new DomainError('CAPABILITY_UNAVAILABLE','Audio settings were not negotiated.',400);
     if(message.microphoneGainDb!==undefined&&c.protocolMinor<5)throw new DomainError('AUDIO_PROTOCOL','Microphone gain was not negotiated.',400);
     if((message.allowPlaybackMic!==undefined||message.followupSeconds!==undefined)&&c.protocolMinor<6)throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings were not negotiated.',400);
     if(c.protocolMinor>=6&&(message.allowPlaybackMic===undefined||message.followupSeconds===undefined))throw new DomainError('AUDIO_PROTOCOL','Conversation audio settings are missing.',400);
     c.audioSettings={volume:message.volume,muted:message.muted,...(message.microphoneGainDb!==undefined?{microphoneGainDb:message.microphoneGainDb}:{}),...(message.allowPlaybackMic!==undefined?{allowPlaybackMic:message.allowPlaybackMic}:{}),...(message.followupSeconds!==undefined?{followupSeconds:message.followupSeconds}:{})};this.voice?.setFollowupSeconds(c.identity.deviceId,c.audioSettings.followupSeconds??0);this.events.emit('audio_settings',c.identity,c.audioSettings);return;
    }
    if(message.type==='eye_settings'){
     if(c.protocolMinor<10||!c.capabilities.includes('eyes'))throw new DomainError('EYE_PROTOCOL','Eye settings were not negotiated.',400);
     c.eyeSettings={design:message.design};this.events.emit('eye_settings',c.identity,c.eyeSettings);return;
    }
    if(message.type==='display_settings'){
     if(c.protocolMinor<13)throw new DomainError('DISPLAY_PROTOCOL','Display settings were not negotiated.',400);
     c.displaySettings={showBatteryIcon:message.showBatteryIcon};this.events.emit('display_settings',c.identity,c.displaySettings);return;
    }
    if(message.type==='battery_status'){
     if(c.protocolMinor<9)throw new DomainError('BATTERY_PROTOCOL','Battery status was not negotiated.',400);
     const {type:_,...status}=message;c.batteryStatus=BatteryStatus.parse(status);this.events.emit('battery_status',c.identity,c.batteryStatus);return;
    }
    if(message.type==='head_calibration'){
     if(c.protocolMinor<8||!c.capabilities.includes('head'))throw new DomainError('CAPABILITY_UNAVAILABLE','Head calibration was not negotiated.',400);
     const {type:_,...calibration}=message;c.headCalibration=HeadCalibration.parse(calibration);this.events.emit('head_calibration',c.identity,c.headCalibration);return;
    }
    if(message.type==='command_status'){await this.persistence.acknowledge(c.identity,c.bootId,message.id,message.status);this.events.emit('command',c.identity,{id:message.id,status:message.status,code:message.code});return;}
    if(message.type==='sensor'){
     if(!c.capabilities.includes('imu'))throw new DomainError('CAPABILITY_UNAVAILABLE','No IMU was negotiated.',400);
     if(c.candidate!==message.pickedUp){c.candidate=message.pickedUp;c.candidateSince=Date.now();}
     if(c.stablePickedUp!==message.pickedUp&&Date.now()-c.candidateSince>=150){c.stablePickedUp=message.pickedUp;this.events.emit('sensor',c.identity,{type:message.pickedUp?'picked_up':'put_down'});}return;
    }
    if(message.type==='voice_stop'){await this.voice?.stop(c.identity.deviceId);return;}
    if(message.type==='voice_playback_done'){if(c.protocolMinor<6)throw new DomainError('AUDIO_PROTOCOL','Playback acknowledgement was not negotiated.',400);this.voice?.playbackDone(c.identity.deviceId,message.interactionId);return;}
    if(message.type==='link_diagnostics'){if(c.protocolMinor<12)throw new DomainError('DIAGNOSTICS_PROTOCOL','Link diagnostics require protocol 1.12.',400);c.linkDiagnostics=message;c.audioTimeouts=message.audioTimeouts;return;}
    if(message.type==='voice_interrupt'){await this.voice?.interrupt(c.identity.deviceId,message.reason==='wake'?'wake':'manual');return;}
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
  socket.on('close',(code,reason)=>{closed=true;clearTimeout(setup);clearInterval(heartbeat);if(c&&this.online.get(c.identity.deviceId)===c){c.closed=true;this.online.delete(c.identity.deviceId);this.recent.set(c.identity.deviceId,{connection:c,expires:Date.now()+30000});setTimeout(()=>{if(this.recent.get(c!.identity.deviceId)?.connection===c)this.recent.delete(c!.identity.deviceId);},30000).unref();req.log.info({deviceId:c.identity.deviceId,bootId:c.bootId,code,reason:reason.toString(),connectedMs:Date.now()-c.openedAt,audioReceivedBytes:c.audioReceivedBytes},'Device connection closed');void this.voice?.disconnect(c.identity.deviceId);this.events.emit('offline',c.identity);}});
 });}
}
