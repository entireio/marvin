import type { RepoCard,Repository } from '../../contracts/src/index.js';
/** UI/runtime contract. Deployment-specific credentials and transports stay behind it. */
export interface RepositoryIntegration {
 readonly deployment:'local'|'hosted';
 configured(ownerId:string):Promise<Repository[]>;
 connect(ownerId:string):Promise<Repository[]>;
 check(ownerId:string,repositoryId:string,signal?:AbortSignal):Promise<unknown>;
 discover(ownerId:string,cursor?:string):Promise<{items:Repository[];nextCursor:string|null}>;
 read(ownerId:string,repositoryId:string,tool:string,args:Record<string,unknown>,signal?:AbortSignal):Promise<RepoCard>;
 invalidate(ownerId:string):void;
}
