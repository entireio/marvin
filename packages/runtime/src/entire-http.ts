import {DomainError,RepoCard,type Repository} from '../../contracts/src/index.js';
import type {RepositoryIntegration} from './repository-integration.js';

type ResponseError={error?:{code?:string;message?:string;status?:number}};
export class EntireHttp implements RepositoryIntegration {
 readonly deployment='local' as const;
 constructor(readonly origin:string,private token:string){}
 private async call<T>(operation:string,body:Record<string,unknown>,signal?:AbortSignal):Promise<T>{
  let response:Response;
  try{response=await fetch(`${this.origin}/v1/${operation}`,{method:'POST',headers:{authorization:`Bearer ${this.token}`,'content-type':'application/json'},body:JSON.stringify(body),signal:signal??AbortSignal.timeout(20000)});}catch{throw new DomainError('ENTIRE_HOST_UNAVAILABLE','The Entire connection on this computer is not running.',503);}
  const value=await response.json().catch(()=>({})) as ResponseError&T;
  if(!response.ok)throw new DomainError(value.error?.code??'ENTIRE_HOST_UNAVAILABLE',value.error?.message??'The Entire connection on this computer could not complete the request.',value.error?.status??response.status);
  return value;
 }
 configured(ownerId:string){return this.call<Repository[]>('configured',{ownerId});}
 connect(ownerId:string){return this.call<Repository[]>('connect',{ownerId});}
 check(ownerId:string,repositoryId:string,signal?:AbortSignal){return this.call<unknown>('check',{ownerId,repositoryId},signal);}
 discover(ownerId:string,cursor?:string){return this.call<{items:Repository[];nextCursor:string|null}>('discover',{ownerId,...(cursor?{cursor}:{})});}
 async read(ownerId:string,repositoryId:string,tool:string,args:Record<string,unknown>,signal?:AbortSignal){return RepoCard.parse(await this.call<unknown>('read',{ownerId,repositoryId,tool,args},signal));}
 invalidate(ownerId:string){void this.call('invalidate',{ownerId}).catch(()=>{});}
}
