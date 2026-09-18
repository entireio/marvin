import { z } from 'zod';
import { Id } from './index.js';
export const DeviceCapability=z.enum(['voice','eyes','gaze','head','tracks','motion','imu','cliff','range','bench_tracks']);
export type DeviceCapability=z.infer<typeof DeviceCapability>;
export const PetAudioSettings=z.object({volume:z.number().int().min(0).max(100),muted:z.boolean(),microphoneGainDb:z.number().int().min(0).max(36).multipleOf(6).optional(),allowPlaybackMic:z.boolean().optional(),followupSeconds:z.number().int().min(0).max(30).optional()}).strict();
export type PetAudioSettings=z.infer<typeof PetAudioSettings>;
export const DeviceHello=z.object({type:z.literal('hello'),protocol:z.object({major:z.number().int(),minor:z.number().int()}).strict(),deviceId:Id,bootId:Id,capabilities:z.array(DeviceCapability).max(8),firmware:z.string().max(64),audioInputRate:z.literal(16000).optional(),audioSettings:PetAudioSettings.optional()}).strict();
export const DeviceControl=z.discriminatedUnion('type',[
 DeviceHello,
 z.object({type:z.literal('heartbeat'),seq:z.number().int().nonnegative()}).strict(),
 z.object({type:z.literal('command_status'),id:Id,status:z.enum(['accepted','completed','failed','cancelled']),code:z.string().max(80).optional()}).strict(),
 z.object({type:z.literal('sensor'),pickedUp:z.boolean()}).strict(),
 z.object({type:z.literal('voice_start'),reason:z.enum(['wake','button'])}).strict(),
 z.object({type:z.literal('voice_stop')}).strict(),
 z.object({type:z.literal('voice_interrupt'),reason:z.literal('wake').optional()}).strict(),
 z.object({type:z.literal('audio_settings'),volume:z.number().int().min(0).max(100),muted:z.boolean(),microphoneGainDb:z.number().int().min(0).max(36).multipleOf(6).optional(),allowPlaybackMic:z.boolean().optional(),followupSeconds:z.number().int().min(0).max(30).optional()}).strict(),
 z.object({type:z.literal('voice_playback_done'),interactionId:Id}).strict()
]);
export const Actions={
 eyes:z.object({expression:z.enum(['neutral','listening','thinking','speaking','concerned','sleeping'])}).strict(),
 gaze:z.object({x:z.number().min(-1).max(1),y:z.number().min(-1).max(1),durationMs:z.number().int().min(100).max(3000)}).strict(),
 head:z.object({yaw:z.number().min(-30).max(30),pitch:z.number().min(-20).max(20),durationMs:z.number().int().min(100).max(3000)}).strict(),
 tracks:z.object({left:z.number().min(-0.3).max(0.3),right:z.number().min(-0.3).max(0.3),durationMs:z.number().int().min(50).max(500)}).strict(),
 motion:z.object({primitive:z.enum(['forward_bit','turn_right','turn_left','move_around'])}).strict()
};
export type DeviceAction=keyof typeof Actions;
export type DeviceCommand={type:'command';id:string;deviceId:string;epoch:number;bootId:string;action:DeviceAction;args:Record<string,unknown>;deadline:number};
