import {randomUUID} from 'node:crypto';
import {z} from 'zod';
import type WebSocket from 'ws';
import {DomainError,Id,RepoCard,type Repository} from '../../../packages/contracts/src/index.js';
import type {Store} from '../../../packages/persistence/src/store.js';
import type {RepositoryIntegration} from '../../../packages/runtime/src/repository-integration.js';

const RepositorySchema=z.object({id:Id,name:z.string().min(1).max(300),description:z.string().max(500),source:z.literal('entire').optional(),capabilities:z.array(z.string().max(40)).max(10).optional()}).strict();
const connectorRequestTimeoutMs=60_000;
const FirstMessage=z.discriminatedUnion('type',[
 z.object({v:z.literal(1),type:z.literal('pair'),token:z.string().min(32).max(256),name:z.string().min(1).max(80)}).strict(),
 z.object({v:z.literal(1),type:z.literal('hello'),connectorId:Id,credential:z.string().min(32).max(256)}).strict()
]);
const Result=z.object({v:z.literal(1),type:z.literal('result'),requestId:Id,ok:z.boolean(),value:z.unknown().optional(),error:z.object({code:z.string().max(80),message:z.string().max(300),status:z.number().int().min(400).max(599)}).optional()}).strict();
type Active={socket:WebSocket;connectorId:string;pending:Map<string,{resolve:(value:unknown)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>};

export class EntireConnectorBroker implements RepositoryIntegration{
 readonly deployment='hosted' as const;
 private active=new Map<string,Active>();
 constructor(readonly store:Store){}
 online(ownerId:string){return this.active.has(ownerId);}
 attach(socket:WebSocket){let ownerId='',active:Active|undefined,authenticated=false;const close=(code:number,message:string)=>{try{socket.close(code,message.slice(0,100));}catch{}}
  const timer=setTimeout(()=>close(4408,'Connector authentication timed out'),15000);
  socket.on('message',raw=>{void(async()=>{if(!authenticated){const message=FirstMessage.parse(JSON.parse(raw.toString()));let connector:{id:string;ownerId:string;name:string};if(message.type==='pair'){const owner=await this.store.consumeEntireConnectorPairing(message.token);if(!owner)throw new DomainError('PAIRING_INVALID','Pairing expired. Create a new code in Marvin.',401);const registered=await this.store.registerEntireConnector(owner,message.name);connector={id:registered.id,ownerId:owner,name:registered.name};socket.send(JSON.stringify({v:1,type:'paired',connectorId:registered.id,credential:registered.credential}));await this.store.setEntireConnection(owner,{authKind:'connector',status:'pending'});}else{const found=await this.store.authenticateEntireConnector(message.connectorId,message.credential);if(!found)throw new DomainError('CONNECTOR_UNAUTHENTICATED','Connector authorization failed.',401);connector=found;}
     ownerId=connector.ownerId;const previous=this.active.get(ownerId);if(previous&&previous.socket!==socket)previous.socket.close(4001,'Another connector came online');active={socket,connectorId:connector.id,pending:new Map()};this.active.set(ownerId,active);authenticated=true;clearTimeout(timer);socket.send(JSON.stringify({v:1,type:'ready'}));return;}
    const result=Result.parse(JSON.parse(raw.toString()));const pending=active?.pending.get(result.requestId);if(!pending)return;clearTimeout(pending.timer);active!.pending.delete(result.requestId);if(result.ok)pending.resolve(result.value);else{if(result.error?.code==='ENTIRE_REAUTH_REQUIRED'){await this.store.setEntireConnection(ownerId,{authKind:'connector',status:'reauth_required'});await this.store.setEntireState(ownerId,'disconnected');}pending.reject(new DomainError(result.error?.code??'ENTIRE_CONNECTOR_FAILED',result.error?.message??'The Entire connector could not complete this read.',result.error?.status??502));}})().catch(error=>close(error instanceof DomainError?4401:4400,error instanceof DomainError?error.message:'Invalid connector message'));});
  socket.on('close',()=>{clearTimeout(timer);if(ownerId&&this.active.get(ownerId)?.socket===socket)this.active.delete(ownerId);for(const pending of active?.pending.values()??[]){clearTimeout(pending.timer);pending.reject(new DomainError('ENTIRE_CONNECTOR_OFFLINE','The Entire connector went offline.',503));}});socket.on('error',()=>close(1011,'Connector failed'));
 }
 private request(ownerId:string,operation:string,body:Record<string,unknown>,signal?:AbortSignal){const active=this.active.get(ownerId);if(!active||active.socket.readyState!==active.socket.OPEN)throw new DomainError('ENTIRE_CONNECTOR_OFFLINE','Open the Marvin Entire Connector on your computer.',503);const requestId=randomUUID();return new Promise<unknown>((resolve,reject)=>{const finish=(error:Error)=>{const pending=active.pending.get(requestId);if(!pending)return;clearTimeout(pending.timer);active.pending.delete(requestId);reject(error);};const timer=setTimeout(()=>finish(new DomainError('ENTIRE_CONNECTOR_TIMEOUT','Entire took too long to answer. Try the repository question again.',504)),connectorRequestTimeoutMs);active.pending.set(requestId,{resolve,reject,timer});signal?.addEventListener('abort',()=>{active.socket.send(JSON.stringify({v:1,type:'cancel',requestId}));finish(new DomainError('ENTIRE_CANCELLED','Repository reading was cancelled.',409));},{once:true});active.socket.send(JSON.stringify({v:1,type:'request',requestId,operation,body,deadline:Date.now()+connectorRequestTimeoutMs}));});}
 async configured(ownerId:string){return z.array(RepositorySchema).max(500).parse(await this.request(ownerId,'configured',{}));}
 async connect(ownerId:string){const repos=z.array(RepositorySchema).max(500).parse(await this.request(ownerId,'connect',{}));if(!repos.length)throw new DomainError('ENTIRE_NOT_CONFIGURED','No Entire repositories are available through this connector.',409);await this.store.setEntireConnection(ownerId,{authKind:'connector',status:'connected'});await this.store.setEntireState(ownerId,'connected');return repos;}
 async check(ownerId:string,repositoryId:string,signal?:AbortSignal){return this.request(ownerId,'check',{repositoryId},signal);}
 async discover(ownerId:string,cursor?:string){return z.object({items:z.array(RepositorySchema).max(500),nextCursor:z.string().max(4096).nullable()}).parse(await this.request(ownerId,'discover',{...(cursor?{cursor}:{})}));}
 async read(ownerId:string,repositoryId:string,tool:string,args:Record<string,unknown>,signal?:AbortSignal){return RepoCard.parse(await this.request(ownerId,'read',{repositoryId,tool,args},signal));}
 invalidate(_ownerId:string){}
 disconnect(ownerId:string){const active=this.active.get(ownerId);active?.socket.close(4002,'Connector disconnected');this.active.delete(ownerId);}
 closeAll(){for(const ownerId of this.active.keys())this.disconnect(ownerId);}
}
