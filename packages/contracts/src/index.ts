import { z } from 'zod';
export const Id = z.string().min(1).max(128).regex(/^[a-zA-Z0-9_-]+$/);
export const Surface = z.enum(['web_text', 'web_voice', 'body_voice']);
export type Surface = z.infer<typeof Surface>;
export const SendTurn = z.object({ conversationId: Id, interactionId: Id, text: z.string().trim().min(1).max(12000), routeId: Id }).strict();
export const ClientEvent = z.discriminatedUnion('type', [
 z.object({ v: z.literal(1), type: z.literal('subscribe'), conversationId: Id, routeId: Id, after: z.number().int().min(0).max(Number.MAX_SAFE_INTEGER).default(0) }).strict(),
 z.object({ v: z.literal(1), type: z.literal('ping') }).strict()
]);
export const RepoCard = z.object({ kind: z.literal('repository'), title: z.string().max(120), repositoryId: Id, revision: z.string().max(80), summary: z.string().max(3000), source: z.enum(['fixture', 'entire']), files: z.array(z.object({ path: z.string().max(300), description: z.string().max(500) })).max(10) });
export type RepoCard = z.infer<typeof RepoCard>;
export const AgentEvent = z.object({ v: z.literal(1), interactionId: Id, conversationId: Id, routeId: Id, seq: z.number().int().nonnegative(), type: z.enum(['started','delta','tool','card','completed','cancelled','error']), text: z.string().max(16000).optional(), card: RepoCard.optional(), code: z.string().max(80).optional() }).strict();
export type AgentEvent = z.infer<typeof AgentEvent>;
export type BodyState = { deviceId: string; status: 'online' | 'offline'; capabilities: string[]; simulated?: boolean; network?: string } | null;
export type InteractionContext = { ownerId: string; conversationId: string; interactionId: string; routeId: string; surface: Surface; repositoryId: string | null; entireState: 'disconnected' | 'fixture' | 'connected'; body: BodyState };
export type Turn = { id: string; conversationId: string; userText: string; assistantText: string; status: 'running'|'completed'|'cancelled'|'failed'; surface: Surface; routeId: string; createdAt: number; ordinal: number; lastSeq: number; cards: RepoCard[] };
export type Conversation = { id: string; title: string; repositoryId: string | null; updatedAt: number; summary: string; summaryThrough: number };
export const RepositorySelection = z.object({ repositoryId: z.enum(['marvin-firmware','marvin-portal']).nullable() }).strict();
export const Network = z.object({ id: Id, ssid: z.string().max(128), rssi: z.number().min(-120).max(0), channel: z.number().int().min(1).max(14), security: z.enum(['wpa2-personal','wpa3-personal','open']), compatible: z.boolean() }).strict();
export type Network = z.infer<typeof Network>;
export const ScanResult = z.object({ v: z.literal(1), source: z.literal('robot'), deviceId: Id, scanId: Id, scannedAt: z.number().nonnegative(), networks: z.array(Network).max(50) }).strict();
export type ScanResult = z.infer<typeof ScanResult>;
export const BodyHello = z.object({ v: z.literal(1), type: z.literal('hello'), deviceId: Id, firmware: z.string().max(40), capabilities: z.array(z.enum(['microphone','speaker','eyes','head','drive','imu','tof','cliff'])).max(8) }).strict();
export class DomainError extends Error { constructor(public code: string, message: string, public status = 400) { super(message); } }
