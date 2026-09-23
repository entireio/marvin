import {DomainError} from '../../contracts/src/index.js';
import type {Store} from '../../persistence/src/store.js';
import type {EntireHostedCli} from './entire-hosted.js';
import type {RepositoryIntegration} from './repository-integration.js';

export class EntireCloudRouter implements RepositoryIntegration{
 readonly deployment='hosted' as const;
 constructor(readonly store:Store,readonly hosted:EntireHostedCli|undefined,readonly connector:RepositoryIntegration|undefined){}
 private async target(ownerId:string,allowPending=false){const connection=await this.store.entireConnection(ownerId);if(!connection){if(allowPending)return null;throw new DomainError('ENTIRE_NOT_CONFIGURED','Connect Entire first.',409);}if(!allowPending&&connection.status==='reauth_required')throw new DomainError('ENTIRE_REAUTH_REQUIRED','Reconnect Entire to continue.',409);if(connection.authKind==='hosted_cli'&&this.hosted)return this.hosted;if(connection.authKind==='connector'&&this.connector)return this.connector;throw new DomainError('ENTIRE_NOT_CONFIGURED','The selected Entire connection method is unavailable.',409);}
 async configured(ownerId:string){const target=await this.target(ownerId,true);return target?target.configured(ownerId):[];}
 private async required(ownerId:string){const target=await this.target(ownerId);if(!target)throw new DomainError('ENTIRE_NOT_CONFIGURED','Connect Entire first.',409);return target;}
 async connect(ownerId:string){return (await this.required(ownerId)).connect(ownerId);}
 async check(ownerId:string,repositoryId:string,signal?:AbortSignal){return (await this.required(ownerId)).check(ownerId,repositoryId,signal);}
 async discover(ownerId:string,cursor?:string){return (await this.required(ownerId)).discover(ownerId,cursor);}
 async read(ownerId:string,repositoryId:string,tool:string,args:Record<string,unknown>,signal?:AbortSignal){return (await this.required(ownerId)).read(ownerId,repositoryId,tool,args,signal);}
 invalidate(ownerId:string){this.hosted?.invalidate(ownerId);this.connector?.invalidate(ownerId);}
}
