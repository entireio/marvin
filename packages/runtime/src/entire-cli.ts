import { execFile } from 'node:child_process';
import { readFile,realpath } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { createHash } from 'node:crypto';
import { z } from 'zod';
import type { RepositoryIntegration } from './repository-integration.js';
import { repositoryArguments,validateRepositoryArguments } from './policy.js';
import { DomainError,RepoCard,type Repository,type Evidence } from '../../contracts/src/index.js';

const Slug=z.string().regex(/^[a-zA-Z0-9][a-zA-Z0-9_.-]*\/[a-zA-Z0-9][a-zA-Z0-9_.-]*$/).max(200);
export const Binding=z.object({ownerId:z.string().min(1),context:z.string().min(1).max(100),configDir:z.string().refine(isAbsolute).optional(),repositories:z.array(z.object({name:Slug,kind:z.enum(['mirror','native']).default('mirror'),checkout:z.string().refine(isAbsolute).optional(),graph:z.boolean().default(false)}).strict()).min(1).max(50)}).strict();
export const Bindings=z.array(Binding).max(100);
type BindingType=z.infer<typeof Binding>;
type BoundRepo=BindingType['repositories'][number];
export type RunCommand=(file:string,args:string[],options:{cwd?:string;env:NodeJS.ProcessEnv;signal?:AbortSignal;maxBytes?:number})=>Promise<string>;
let activeProcesses=0;
export const runCommand:RunCommand=(file,args,options)=>new Promise((resolve,reject)=>{
 if(activeProcesses>=4){reject(new DomainError('ENTIRE_BUSY','Repository reads are busy. Retry shortly.',429));return;}activeProcesses++;
 const child=execFile(file,args,{...options,timeout:20000,maxBuffer:options.maxBytes??262144,encoding:'utf8',killSignal:'SIGKILL',windowsHide:true},(error,stdout,stderr)=>{
  activeProcesses--;
  if(!error){resolve(stdout);return;}
  const diagnostic=String(stderr??'');
  if(error.name==='AbortError'){reject(new DomainError('ENTIRE_CANCELLED','Repository reading was cancelled.',409));return;}
  if(/not logged in|log in|login required|401|token.*expir|context.*not found|no.*context|keychain|keyring/i.test(diagnostic))reject(new DomainError('ENTIRE_REAUTH_REQUIRED','Reconnect Entire on the server, then retry.',409));
  else if(/403|forbidden|not authorized|no repo matching|404|not found|no requested repo/i.test(diagnostic))reject(new DomainError('REPOSITORY_FORBIDDEN','This repository is unavailable or no longer accessible.',403));
  else if(/429|rate.limit/i.test(diagnostic))reject(new DomainError('ENTIRE_RATE_LIMITED','Entire is limiting requests. Wait a moment and retry.',429));
  else if(error.killed||error.code==='ERR_CHILD_PROCESS_STDIO_MAXBUFFER')reject(new DomainError('ENTIRE_LIMIT','Entire exceeded the time or response-size limit. Narrow the request.',502));
  else if(error.code==='ENOENT')reject(new DomainError('ENTIRE_UNAVAILABLE','The configured Entire CLI is unavailable on the server.',503));
  else reject(new DomainError('ENTIRE_UNAVAILABLE','Entire could not complete this read. Your conversation is still available.',502));
 });
 child.stdin?.end();
});
export const repositoryId=(name:string)=>'entire_'+createHash('sha256').update(name).digest('hex').slice(0,24);
const remoteName=(value:string)=>value.replace(/^(?:gh|et)\//,'');
const MirrorPage=z.object({items:z.array(z.object({repo:z.string(),status:z.string()})).max(500),nextPageToken:z.string().optional()});
const parse=(raw:string)=>{try{return JSON.parse(raw) as unknown;}catch{throw new DomainError('ENTIRE_BAD_RESPONSE','Entire returned an unreadable response. Retry or check the CLI version.',502);}};
const validate=<T>(schema:z.ZodType<T>,value:unknown):T=>{const result=schema.safeParse(value);if(!result.success)throw new DomainError('ENTIRE_BAD_RESPONSE','Entire returned an unsupported response shape.',502);return result.data;};
const text=(value:string,max=1600)=>value.replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----/g,'[private key redacted]').replace(/\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,})\b/g,'[credential redacted]').slice(0,max);
export const SourcePath=z.string().min(1).max(300).refine(p=>!p.startsWith('/')&&!p.includes('\\')&&!/[\x00-\x1f\x7f]/.test(p)&&p.split('/').every(s=>s!=='.'&&s!=='..')&&!/(^|\/)(\.git|\.env(?:\..*)?|\.ssh|\.npmrc|credentials|id_rsa|id_ed25519|.*\.(pem|key|p12))($|\/)/i.test(p),'Choose a regular source file, not a secret or internal path.');
export class EntireCli implements RepositoryIntegration {
 readonly deployment="local" as const;
 private cache=new Map<string,RepoCard>();
 invalidate(ownerId:string){for(const key of this.cache.keys())if(key.startsWith(ownerId+"|"))this.cache.delete(key);}
 constructor(readonly executable:string,readonly bindingsFile:string,private run:RunCommand=runCommand){}
 async binding(ownerId:string){let value:unknown;try{value=JSON.parse(await readFile(this.bindingsFile,'utf8'));}catch{throw new DomainError('ENTIRE_NOT_CONFIGURED','Entire repository access has not been configured on this server.',409);}const bindings=validate(Bindings,value);const matching=bindings.filter(b=>b.ownerId===ownerId);if(matching.length!==1)throw new DomainError('ENTIRE_NOT_CONFIGURED','Entire repository access has not been configured for your account.',409);return matching[0];}
 private env(binding:BindingType){return {PATH:process.env.PATH,HOME:process.env.HOME,TMPDIR:process.env.TMPDIR,LANG:'en_US.UTF-8',ENTIRE_TELEMETRY_OPTOUT:'1',GIT_TERMINAL_PROMPT:'0',GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:'/dev/null',GIT_PAGER:'cat',PAGER:'cat',...(binding.configDir?{ENTIRE_CONFIG_DIR:binding.configDir}:{})};}
 private cli(binding:BindingType,args:string[],cwd?:string,signal?:AbortSignal){return this.run(this.executable,['--context',binding.context,...args],{cwd,env:this.env(binding),signal});}
 private repository(repo:BoundRepo):Repository{return {id:repositoryId(repo.name),name:repo.name,description:repo.checkout?'Entire + committed local source':'Entire repository',source:'entire',capabilities:['summary','search','code_search',...(repo.checkout?['source','history','checkpoint','session']:[]),...(repo.checkout&&repo.graph?['graph']:[])]};}
 private async mirrorPage(binding:BindingType,cursor?:string,signal?:AbortSignal){const args=['repo','mirror','list','--mirrored','--json','--page-size','100'];if(cursor)args.push('--page-token',z.string().max(2048).parse(cursor));return validate(MirrorPage,parse(await this.cli(binding,args,undefined,signal)));}
 private async mirrored(binding:BindingType,signal?:AbortSignal){const items:{repo:string;status:string}[]=[];let cursor:string|undefined;for(let pageNumber=0;pageNumber<10;pageNumber++){const page=await this.mirrorPage(binding,cursor,signal);items.push(...page.items);if(!page.nextPageToken)return items;cursor=page.nextPageToken;}throw new DomainError('ENTIRE_LIMIT','Entire returned too many repository pages. Narrow the connected account.',502);}
 private boundMirror(binding:BindingType,name:string):BoundRepo{return binding.repositories.find(r=>r.kind==='mirror'&&r.name===name)??{name,kind:'mirror',graph:false};}
 async configured(ownerId:string):Promise<Repository[]>{const b=await this.binding(ownerId),mirrors=await this.mirrored(b),repos=new Map<string,BoundRepo>();for(const item of mirrors){const name=remoteName(item.repo);if(Slug.safeParse(name).success)repos.set(name,this.boundMirror(b,name));}for(const repo of b.repositories)if(repo.kind==='native')repos.set(repo.name,repo);return [...repos.values()].sort((a,b)=>a.name.localeCompare(b.name)).map(r=>this.repository(r));}
 async check(ownerId:string,id:string,signal?:AbortSignal){const b=await this.binding(ownerId);let repo=b.repositories.find(r=>repositoryId(r.name)===id);if(!repo){const match=(await this.mirrored(b,signal)).map(item=>remoteName(item.repo)).find(name=>repositoryId(name)===id);if(match&&Slug.safeParse(match).success)repo=this.boundMirror(b,match);}if(!repo)throw new DomainError('REPOSITORY_FORBIDDEN','This repository is unavailable in the connected Entire account.',403);
  if(repo.kind==='mirror'){const info=validate(z.object({repo:z.string(),status:z.string()}),parse(await this.cli(b,['repo','mirror','get',repo.name,'--json'],undefined,signal)));if(remoteName(info.repo)!==repo.name)throw new DomainError('ENTIRE_SCOPE_MISMATCH','Entire returned a different repository. No content was used.',502);}
  else {const [project,name]=repo.name.split('/');const info=validate(z.object({name:z.string()}),parse(await this.cli(b,['repo','get',name,'--project',project,'--json'],undefined,signal)));if(info.name!==name)throw new DomainError('ENTIRE_SCOPE_MISMATCH','Entire returned a different repository. No content was used.',502);}
  return {binding:b,repo};
 }
 async connect(ownerId:string){const repos=await this.configured(ownerId);if(!repos.length)throw new DomainError('ENTIRE_NOT_CONFIGURED','No repositories have been configured.',409);await this.check(ownerId,repos[0].id);return repos;}
 async discover(ownerId:string,cursor?:string){const b=await this.binding(ownerId),page=await this.mirrorPage(b,cursor);return {items:page.items.map(item=>{const name=remoteName(item.repo);return {...this.repository(this.boundMirror(b,name)),description:item.status};}),nextCursor:page.nextPageToken||null};}
 private git(binding:BindingType,repo:BoundRepo,args:string[],signal?:AbortSignal){return this.run('git',['--no-pager',...args],{cwd:repo.checkout,env:this.env(binding),signal,maxBytes:524288});}
 private async snapshot(binding:BindingType,repo:BoundRepo,signal?:AbortSignal){if(!repo.checkout)throw new DomainError('ENTIRE_CAPABILITY_UNAVAILABLE','Committed source requires an authorized local checkout on the server.',409);
  const root=(await this.git(binding,repo,['rev-parse','--show-toplevel'],signal)).trim();if(await realpath(root)!==await realpath(repo.checkout))throw new DomainError('ENTIRE_CHECKOUT_MISMATCH','The configured checkout must be the repository root.',409);
  const origin=(await this.git(binding,repo,['config','--get','remote.origin.url'],signal)).trim();
  // Require the authorized repository identity. No user-provided URL is fetched or executed.
  const matches=origin===`https://github.com/${repo.name}.git`||origin===`https://github.com/${repo.name}`||origin===`git@github.com:${repo.name}.git`||new RegExp(`^entire://[a-zA-Z0-9.-]+/(?:gh|et)/${repo.name.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')}(?:\\.git)?$`).test(origin);
  if(!matches)throw new DomainError('ENTIRE_CHECKOUT_MISMATCH','The checkout origin does not match the authorized repository.',409);
  const revision=(await this.git(binding,repo,['rev-parse','--verify','HEAD^{commit}'],signal)).trim();if(!/^[a-f0-9]{40,64}$/.test(revision))throw new DomainError('ENTIRE_BAD_RESPONSE','The checkout has no valid commit.',409);return revision;
 }
 private card(repo:BoundRepo,id:string,title:string,summary:string,evidence:Evidence[]=[],revision='index revision unavailable',extra:Partial<RepoCard>={}):RepoCard{return RepoCard.parse({kind:'repository',repositoryId:id,source:'entire',title,summary:text(summary,3000),revision,files:[],fetchedAt:Date.now(),evidence,...extra});}
 async read(ownerId:string,id:string,tool:string,args:Record<string,unknown>,signal?:AbortSignal):Promise<RepoCard>{
  if(!(tool in repositoryArguments))throw new DomainError('TOOL_FORBIDDEN','Unknown repository operation.',403);
  validateRepositoryArguments(tool as keyof typeof repositoryArguments,args);
  const {binding:b,repo}=await this.check(ownerId,id,signal);signal?.throwIfAborted();
  if(tool==='repository_summary'){
   let revision='index revision unavailable',evidence:Evidence[]=[];
   if(repo.checkout){revision=await this.snapshot(b,repo,signal);const files=(await this.git(b,repo,['ls-tree','-r','--name-only',revision],signal)).trim().split('\n').filter(p=>SourcePath.safeParse(p).success).slice(0,10);evidence=files.map((path,i)=>({id:`file-${i+1}`,label:path,path,origin:'git',revision}));}
   return this.card(repo,id,repo.name,repo.checkout?'Entire access verified. These are files in the authorized local checkout at the displayed revision. Read source or search Entire before making implementation claims.':'Entire access verified. Use indexed search for repository content and history. No local source checkout is configured.',evidence,revision,{capabilities:(await this.configured(ownerId)).find(r=>r.id===id)!.capabilities});
  }
  if(tool==='repository_search'){
   const query=String(args.query),page=Number(args.page),mode=String(args.mode);if(/(?:^|\s)(?:repo|author|branch|date)\s*:/i.test(query))throw new DomainError('ENTIRE_SCOPE_FILTER','Repository scope is set by Marvin. Remove inline filters.',400);
   const command=['search',query,'--repo',repo.kind==='native'?`et/${repo.name}`:repo.name,'--limit','10'];if(mode==='code')command.push('--code','--json');else command.push('--compact','--page',String(page));
   const raw=parse(await this.cli(b,command,repo.checkout,signal));
   if(mode==='code'){
    const output=validate(z.object({results:z.array(z.object({repo:z.string(),path:z.string(),line:z.number().int().min(1),context_line:z.string()})).max(1000),failed_jurisdictions:z.array(z.string()).optional(),skipped_repos:z.array(z.unknown()).optional()}),raw);
    if(output.results.some(r=>remoteName(r.repo)!==repo.name))throw new DomainError('ENTIRE_SCOPE_MISMATCH','Entire returned results outside the selected repository.',502);
    const evidence=output.results.slice(0,10).filter(r=>SourcePath.safeParse(r.path).success).map((r,i)=>({id:`code-${i+1}`,label:r.path,path:r.path,lineStart:r.line,lineEnd:r.line,excerpt:text(r.context_line),origin:'entire' as const}));
    return this.card(repo,id,'Code search',evidence.length?'Indexed exact-text matches. The index did not provide a commit revision; verify current source before relying on these snippets.':'No matching indexed code was returned. This does not prove the code is absent.',evidence,undefined,{partial:!!(output.failed_jurisdictions?.length||output.skipped_repos?.length)});
   }
   const output=validate(z.object({results:z.array(z.object({id:z.string(),type:z.string(),repo:z.string(),title:z.string(),snippet:z.string().optional(),date:z.string().optional()})).max(100),total_pages:z.number().optional(),page:z.number().optional(),partial:z.boolean().optional(),truncated:z.boolean().optional(),coverage_incomplete:z.boolean().optional()}),raw);
   if(output.results.some(r=>remoteName(r.repo)!==repo.name))throw new DomainError('ENTIRE_SCOPE_MISMATCH','Entire returned history outside the selected repository.',502);
   return this.card(repo,id,'Repository history search','Indexed history excerpts. Fetch checkpoint details before drawing conclusions about a decision.',output.results.slice(0,10).map(r=>({id:r.id.slice(0,128),label:text(r.title,200),excerpt:text(r.snippet??r.title),origin:'entire',kind:r.type.slice(0,40),date:r.date?.slice(0,80)})),undefined,{nextPage:output.total_pages&&page<output.total_pages?page+1:null,partial:!!(output.partial||output.truncated||output.coverage_incomplete)});
  }
  const revision=await this.snapshot(b,repo,signal);
  if(tool==='repository_source'){
   const path=SourcePath.parse(args.path),start=Number(args.start),end=Number(args.end);
   const cacheKey=[ownerId,id,revision,path,start,end].join('|');const cached=this.cache.get(cacheKey);if(cached)return {...cached,fetchedAt:Date.now()};
   const tree=(await this.git(b,repo,['ls-tree',revision,'--',path],signal)).trim();if(!/^100(?:644|755) blob [a-f0-9]+\t/.test(tree))throw new DomainError('SOURCE_UNAVAILABLE','Choose a regular committed source file.',404);
   const size=Number((await this.git(b,repo,['cat-file','-s',`${revision}:${path}`],signal)).trim());if(!Number.isFinite(size)||size>262144)throw new DomainError('SOURCE_TOO_LARGE','This file exceeds the source-read limit. Use indexed code search.',413);
   const raw=await this.git(b,repo,['show',`${revision}:${path}`],signal);if(raw.includes('\0'))throw new DomainError('SOURCE_BINARY','Binary files are not exposed as source.',400);
   const lines=raw.split('\n'),excerpt=text(lines.slice(start-1,end).map((line,index)=>`${start+index}: ${line}`).join('\n'),8000);if(start>lines.length)throw new DomainError('SOURCE_RANGE','That line range is outside this file.',400);
   const card=this.card(repo,id,path,`Committed source, lines ${start}–${Math.min(end,lines.length)}. Uncommitted changes are excluded.`,[{id:'source',label:path,path,lineStart:start,lineEnd:Math.min(end,lines.length),excerpt,origin:'git',revision}],revision,{partial:excerpt.length>=8000});
   if(this.cache.size>=32)this.cache.delete(this.cache.keys().next().value!);this.cache.set(cacheKey,card);return card;
  }
  if(tool==='repository_history'){
   const raw=await this.cli(b,['checkpoint','explain','--json','--limit','10','--no-pager'],repo.checkout,signal);
   const rows=validate(z.array(z.object({checkpoint_id:z.string(),date:z.string(),message:z.string().optional(),session_count:z.number().optional()})).max(100),parse(raw));
   return this.card(repo,id,'Recent checkpoints',rows.length?'Stored Entire checkpoints for the local branch.':'No stored Entire checkpoints were returned for this branch.',rows.slice(0,10).map(r=>({id:r.checkpoint_id,label:text(r.message??r.checkpoint_id,200),date:r.date,origin:'entire',kind:'checkpoint'})),revision);
  }
  if(tool==='repository_checkpoint'){
   const target=String(args.id);const cp=validate(z.object({checkpoint_id:z.string(),partial:z.boolean().optional(),sessions:z.array(z.object({session_id:z.string().optional(),agent:z.string().optional(),summary:z.object({intent:z.string().optional(),outcome:z.string().optional()}).nullable().optional(),error:z.string().optional()})).max(100)}),parse(await this.cli(b,['checkpoint','explain',target,'--json','--no-pager'],repo.checkout,signal)));
   if(cp.checkpoint_id!==target)throw new DomainError('ENTIRE_SCOPE_MISMATCH','Use a complete checkpoint identifier returned by repository history.',400);
   return this.card(repo,id,'Checkpoint '+target,'Stored checkpoint metadata. Full transcripts are not retrieved.',cp.sessions.slice(0,10).map((s,i)=>({id:s.session_id??`${target}-${i}`,label:`${s.agent??'Agent'} session`,excerpt:text(s.error?'Metadata unavailable':s.summary?[s.summary.intent,s.summary.outcome].filter(Boolean).join('\n'):'No stored summary is available.'),origin:'entire',kind:'session'})),revision,{partial:cp.partial||cp.sessions.some(s=>!!s.error)});
  }
  if(tool==='repository_session'){
   const target=String(args.id);const info=validate(z.object({session_id:z.string(),agent:z.string(),status:z.string(),model:z.string().optional(),last_checkpoint_id:z.string().optional(),files_touched:z.array(z.string()).optional()}),parse(await this.cli(b,['session','info',target,'--json'],repo.checkout,signal)));
   if(info.session_id!==target)throw new DomainError('ENTIRE_SCOPE_MISMATCH','Use a complete session identifier from a checkpoint.',400);
   return this.card(repo,id,'Session metadata',`${info.agent} · ${info.status}. Full transcripts and local paths are excluded.`,[{id:target,label:info.agent,origin:'entire',kind:'session',excerpt:text(JSON.stringify({status:info.status,model:info.model,checkpoint:info.last_checkpoint_id,files:info.files_touched?.filter(p=>SourcePath.safeParse(p).success).slice(0,10)}))}],revision);
  }
  if(tool==='repository_graph'){
   if(!repo.graph)throw new DomainError('ENTIRE_CAPABILITY_UNAVAILABLE','The Entire graph plugin is not configured for this repository.',409);
   // The plugin's output is bounded and treated as quoted evidence, not instructions.
   const raw=await this.cli(b,['graph','search','--repo',repo.checkout!,'--head','--profile','full','--query',String(args.query),'--top-k','5','--max-context-bytes','8000','--format','json'],repo.checkout,signal);
   if(await this.snapshot(b,repo,signal)!==revision)throw new DomainError('ENTIRE_CHECKOUT_CHANGED','The checkout changed during graph reading. Retry for consistent evidence.',409);
   const parsed=parse(raw);if(typeof parsed!=='object'||parsed===null)throw new DomainError('ENTIRE_BAD_RESPONSE','Graph returned invalid data.',502);
   return this.card(repo,id,'Code graph search','Entire graph results from the committed local tree. Quoted output may be truncated.',[{id:'graph',label:'Entire graph',origin:'entire',revision,excerpt:text(JSON.stringify(parsed),8000)}],revision,{partial:raw.length>8000});
  }
  throw new DomainError('TOOL_FORBIDDEN','This repository operation is unavailable.',403);
 }
}
