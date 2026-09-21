import { z } from 'zod';
import { Id } from './index.js';
export const DeviceCapability=z.enum(['voice','eyes','gaze','head','tracks','motion','remote','imu','cliff','range']);
export type DeviceCapability=z.infer<typeof DeviceCapability>;
export const PetAudioSettings=z.object({volume:z.number().int().min(0).max(100),muted:z.boolean(),microphoneGainDb:z.number().int().min(0).max(36).multipleOf(6).optional(),allowPlaybackMic:z.boolean().optional(),followupSeconds:z.number().int().min(0).max(30).optional()}).strict();
export type PetAudioSettings=z.infer<typeof PetAudioSettings>;
export const EyeDesign=z.enum(['classic','solid','friendly']);
export const PetEyeSettings=z.object({design:EyeDesign}).strict();
export type PetEyeSettings=z.infer<typeof PetEyeSettings>;
export const BatteryStatus=z.object({levelPercent:z.number().int().min(0).max(100).nullable(),voltageMv:z.number().int().min(2500).max(5000).nullable(),charging:z.boolean().nullable()}).strict().refine(value=>(value.levelPercent===null)===(value.voltageMv===null),{message:'Battery level and voltage must be available together.'});
export type BatteryStatus=z.infer<typeof BatteryStatus>;
export const HeadCalibration=z.object({yawCenter:z.number().int().min(60).max(120),pitchCenter:z.number().int().min(60).max(120),yawReversed:z.boolean(),pitchReversed:z.boolean()}).strict();
export type HeadCalibration=z.infer<typeof HeadCalibration>;
/** A short, exact line that the owner asks the Desktop Pet to say aloud. */
export const PetSpeech=z.object({text:z.string().trim().min(1).max(500)}).strict();
export type PetSpeech=z.infer<typeof PetSpeech>;
export const DeviceHello=z.object({type:z.literal('hello'),protocol:z.object({major:z.number().int(),minor:z.number().int()}).strict(),deviceId:Id,bootId:Id,capabilities:z.array(DeviceCapability).max(10),firmware:z.string().max(64),audioInputRate:z.literal(16000).optional(),audioSettings:PetAudioSettings.optional(),headCalibration:HeadCalibration.optional(),batteryStatus:BatteryStatus.optional(),eyeSettings:PetEyeSettings.optional()}).strict();
export const DeviceControl=z.discriminatedUnion('type',[
 DeviceHello,
 z.object({type:z.literal('heartbeat'),seq:z.number().int().nonnegative()}).strict(),
 z.object({type:z.literal('command_status'),id:Id,status:z.enum(['accepted','completed','failed','cancelled']),code:z.string().max(80).optional()}).strict(),
 z.object({type:z.literal('sensor'),pickedUp:z.boolean()}).strict(),
 z.object({type:z.literal('voice_start'),reason:z.enum(['wake','button'])}).strict(),
 z.object({type:z.literal('voice_stop')}).strict(),
 z.object({type:z.literal('voice_interrupt'),reason:z.literal('wake').optional()}).strict(),
 z.object({type:z.literal('audio_settings'),volume:z.number().int().min(0).max(100),muted:z.boolean(),microphoneGainDb:z.number().int().min(0).max(36).multipleOf(6).optional(),allowPlaybackMic:z.boolean().optional(),followupSeconds:z.number().int().min(0).max(30).optional()}).strict(),
 z.object({type:z.literal('battery_status'),...BatteryStatus.shape}).strict(),
 z.object({type:z.literal('head_calibration'),...HeadCalibration.shape}).strict(),
 z.object({type:z.literal('eye_settings'),...PetEyeSettings.shape}).strict(),
 z.object({type:z.literal('voice_playback_done'),interactionId:Id}).strict()
]);
export const Actions={
 eyes:z.object({expression:z.enum(['neutral','listening','thinking','speaking','concerned','sleeping'])}).strict(),
 gaze:z.object({x:z.number().min(-1).max(1),y:z.number().min(-1).max(1),durationMs:z.number().int().min(100).max(3000)}).strict(),
 head:z.object({yaw:z.number().min(-40).max(40),pitch:z.number().min(-30).max(30),durationMs:z.number().int().min(100).max(3000)}).strict(),
 tracks:z.object({left:z.number().min(-0.3).max(0.3),right:z.number().min(-0.3).max(0.3),durationMs:z.number().int().min(50).max(30000)}).strict(),
 /* Input intent, not motor duty. Firmware owns mixing, acceleration and the
    short dead-man watchdog so this exact shape can be shared by web and BLE. */
 remote:z.object({throttle:z.number().min(-1).max(1),turn:z.number().min(-1).max(1),headYaw:z.number().min(-1).max(1),headPitch:z.number().min(-1).max(1),autonomousHead:z.boolean(),sequence:z.number().int().nonnegative()}).strict(),
 motion:z.object({primitive:z.enum(['forward_bit','backward_bit','turn_around','turn_right','turn_left','move_around'])}).strict()
};
export type DeviceAction=keyof typeof Actions;
export type DeviceCommand={type:'command';id:string;deviceId:string;epoch:number;bootId:string;action:DeviceAction;args:Record<string,unknown>;deadline:number};
