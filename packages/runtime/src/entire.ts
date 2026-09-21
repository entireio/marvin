import { DomainError, type RepoCard } from '../../contracts/src/index.js';
export const fixtureRepositories=[{id:'marvin-firmware',name:'marvin / firmware',description:'ESP32-S3 firmware · sample repository'},{id:'marvin-portal',name:'marvin / portal',description:'Personal web application · sample repository'}];
export interface EntireAdapter { source:'fixture'|'entire'; repositories(): Promise<typeof fixtureRepositories>; readSummary(repositoryId:string):Promise<RepoCard>; }
export class FixtureEntire implements EntireAdapter {
 source='fixture' as const;
 async repositories(){return fixtureRepositories;}
 async readSummary(id:string):Promise<RepoCard>{
  if(!fixtureRepositories.some(r=>r.id===id)) throw new DomainError('REPOSITORY_FORBIDDEN','Repository is unavailable.',403);
  return {kind:'repository',title:id==='marvin-firmware'?'A clearer path from setup to conversation':'The personal Marvin portal',repositoryId:id,revision:'fixture-001',source:'fixture',summary:'Illustrative repository data for development. The current slice separates Wi-Fi provisioning, conversation state, and interaction-scoped tools.',files:[{path:'provisioning/network.ts',description:'The Desktop Pet scans for compatible 2.4 GHz networks; failed changes keep the previous configuration.'},{path:'runtime/policy.ts',description:'Physical actions are available only through the Desktop Pet.'},{path:'conversation/store.ts',description:'Conversation state remains in Marvin across model sessions.'}]};
 }
}
