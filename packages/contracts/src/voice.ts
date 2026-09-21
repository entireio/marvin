import { z } from 'zod';
import { Id } from './index.js';
export const VoiceControl=z.discriminatedUnion('type',[
 z.object({v:z.literal(1),type:z.literal('start'),conversationId:Id,csrf:z.string().min(1).max(200)}).strict(),
 z.object({v:z.literal(1),type:z.literal('interrupt')}).strict(),
 z.object({v:z.literal(1),type:z.literal('mute'),muted:z.boolean()}).strict(),
 z.object({v:z.literal(1),type:z.literal('stop')}).strict(),
 z.object({v:z.literal(1),type:z.literal('ping')}).strict()
]);
export type VoiceState='connecting'|'listening'|'hearing'|'thinking'|'speaking'|'muted'|'closed'|'error';
