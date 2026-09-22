import WebSocket from 'ws';
import { EventEmitter } from 'node:events';
import { randomUUID } from 'node:crypto';
import { readFileSync,writeFileSync,renameSync,existsSync } from 'node:fs';
import { Actions,type BatteryStatus,type DeviceCommand,type DeviceCapability,type HeadCalibration,type PetEyeSettings } from '../../contracts/src/device.js';
/** Executable body simulator. It never accesses microphone, motor or provider APIs. */
export class DeviceSimulator extends EventEmitter {
 private socket?:WebSocket;private ledger=new Map<string,string>();private timers=new Map<string,ReturnType<typeof setTimeout>>();private previewTimer?:ReturnType<typeof setTimeout>;private seq=0;epoch=0;executions=0;receivedAudioBytes=0;uploadedAudioBytes=0;
 audioSettings:{volume:number;muted:boolean;microphoneGainDb?:number;allowPlaybackMic?:boolean;followupSeconds?:number}={volume:60,muted:false};
 headCalibration:HeadCalibration={yawCenter:90,pitchCenter:90,yawReversed:true,pitchReversed:true};
 headCalibrationPreview:HeadCalibration|null=null;
 batteryStatus:BatteryStatus={levelPercent:72,voltageMv:3940,charging:null};
 eyeSettings:PetEyeSettings={design:'classic'};
 constructor(readonly deviceId:string,readonly bootId=randomUUID(),readonly capabilities:DeviceCapability[]=['eyes','gaze','head','tracks','imu','cliff'],private ledgerFile?:string){super();if(ledgerFile&&existsSync(ledgerFile))this.ledger=new Map(JSON.parse(readFileSync(ledgerFile,'utf8')));}
 private persist(){if(this.ledgerFile){const tmp=this.ledgerFile+'.pending';writeFileSync(tmp,JSON.stringify([...this.ledger]),{mode:0o600});renameSync(tmp,this.ledgerFile);}}
 async connect(url:string,token:string,minor=1){this.seq=0;if(minor>=5&&this.audioSettings.microphoneGainDb===undefined)this.audioSettings.microphoneGainDb=30;if(minor>=6){this.audioSettings.allowPlaybackMic??=false;this.audioSettings.followupSeconds??=5;}const socket=new WebSocket(url,{headers:{authorization:'Bearer '+token}});this.socket=socket;
  return new Promise<void>((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Simulator handshake timed out')),5000);
   socket.on('open',()=>this.send({type:'hello',protocol:{major:1,minor},deviceId:this.deviceId,bootId:this.bootId,firmware:'simulator-1',capabilities:this.capabilities,...(minor>=4&&this.capabilities.includes('voice')?{audioSettings:this.audioSettings}:{}),...(minor>=8&&this.capabilities.includes('head')?{headCalibration:this.headCalibration}:{}),...(minor>=9?{batteryStatus:this.batteryStatus}:{}),...(minor>=10&&this.capabilities.includes('eyes')?{eyeSettings:this.eyeSettings}:{})}));
   socket.on('message',(raw,binary)=>{if(binary){this.receivedAudioBytes+=raw.toString().length;return;}const event=JSON.parse(raw.toString());if(event.type==='welcome'){this.epoch=event.epoch;clearTimeout(timer);resolve();}if(event.type==='ping')this.send({type:'heartbeat',seq:this.seq++});if(event.type==='audio_settings'){this.audioSettings={volume:event.volume,muted:event.muted,...(event.microphoneGainDb!==undefined?{microphoneGainDb:event.microphoneGainDb}:{}),...(event.allowPlaybackMic!==undefined?{allowPlaybackMic:event.allowPlaybackMic}:{}),...(event.followupSeconds!==undefined?{followupSeconds:event.followupSeconds}:{})};this.send({type:'audio_settings',...this.audioSettings});}if(event.type==='head_calibration'){this.headCalibration={yawCenter:event.yawCenter,pitchCenter:event.pitchCenter,yawReversed:event.yawReversed,pitchReversed:event.pitchReversed};this.headCalibrationPreview=null;if(this.previewTimer)clearTimeout(this.previewTimer);this.send({type:'head_calibration',...this.headCalibration});}if(event.type==='head_calibration_preview'){this.headCalibrationPreview={yawCenter:event.yawCenter,pitchCenter:event.pitchCenter,yawReversed:event.yawReversed,pitchReversed:event.pitchReversed};if(this.previewTimer)clearTimeout(this.previewTimer);this.previewTimer=setTimeout(()=>{this.headCalibrationPreview=null;},2000);}if(event.type==='eye_settings'){this.eyeSettings={design:event.design};this.send({type:'eye_settings',...this.eyeSettings});}if(event.type==='command')this.command(event);if(event.type==='cancel'){clearTimeout(this.timers.get(event.id));this.timers.delete(event.id);if(this.ledger.has(event.id)){this.ledger.set(event.id,'cancelled');this.persist();this.ack(event.id,'cancelled');}}this.emit('event',event);});
   socket.on('error',()=>{clearTimeout(timer);reject(new Error('Simulator connection failed'));});socket.on('close',()=>{clearTimeout(timer);this.emit('disconnected');});
  });
 }
 send(data:unknown){if(this.socket?.readyState===WebSocket.OPEN)this.socket.send(JSON.stringify(data));}
 private ack(id:string,status:string){this.send({type:'command_status',id,status});}
 private command(command:DeviceCommand){
  if(command.deviceId!==this.deviceId||command.epoch!==this.epoch||command.bootId!==this.bootId||command.deadline<=Date.now())return;
  if(!Actions[command.action]?.safeParse(command.args).success||!this.capabilities.includes(command.action))return;
  // Remote samples model the firmware's one-way, non-durable intent stream.
  if(command.action==='remote'){this.executions++;return;}
  const previous=this.ledger.get(command.id);if(previous){this.ack(command.id,previous==='started'?'failed':previous);return;}
  // Write ahead of the simulated side effect. A crash may lose execution, but never retries uncertain motion.
  if(this.ledger.size>=4096){this.ack(command.id,'failed');return;}
  this.ledger.set(command.id,'started');this.persist();this.executions++;this.ack(command.id,'accepted');
  const timer=setTimeout(()=>{this.ledger.set(command.id,'completed');this.persist();this.timers.delete(command.id);this.ack(command.id,'completed');},Number(command.args.durationMs??1));this.timers.set(command.id,timer);
 }
 pickup(pickedUp:boolean){this.send({type:'sensor',pickedUp});}
 setAudioSettings(volume:number,muted:boolean,microphoneGainDb=this.audioSettings.microphoneGainDb){this.audioSettings={...this.audioSettings,volume,muted,...(microphoneGainDb!==undefined?{microphoneGainDb}:{})};this.send({type:'audio_settings',...this.audioSettings});}
 setBatteryStatus(status:BatteryStatus){this.batteryStatus=status;this.send({type:'battery_status',...status});}
 disconnect(){this.socket?.terminate();}
 close(){for(const timer of this.timers.values())clearTimeout(timer);this.timers.clear();if(this.previewTimer)clearTimeout(this.previewTimer);this.socket?.terminate();}
}
