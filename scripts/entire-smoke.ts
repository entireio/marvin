import { config } from '../apps/server/src/config.js';
import { sqliteDatabase } from '../packages/persistence/src/database.js';
import { Store } from '../packages/persistence/src/store.js';
import { EntireCli } from '../packages/runtime/src/entire-cli.js';
import { writeFile,mkdir } from 'node:fs/promises';
try{process.loadEnvFile('.env');}catch{}
const cfg=config();if(!cfg.ENTIRE_CLI_PATH||!cfg.ENTIRE_BINDINGS_FILE||cfg.AUTH_MODE==='oidc')throw new Error('Configure the local Entire integration first. See docs/m4-entire.md.');
const db=sqliteDatabase(cfg.SQLITE_PATH),store=new Store(db);const owners=await db.query<{id:string}>('SELECT id FROM owners WHERE issuer=? AND subject=?',[cfg.AUTH_MODE==='local'?'local':'development','owner']);await db.close();if(owners.length!==1)throw new Error('Sign in to the local portal first.');
const adapter=new EntireCli(cfg.ENTIRE_CLI_PATH,cfg.ENTIRE_BINDINGS_FILE),owner=owners[0].id;
const checks:{operation:string;passed:boolean;error?:string;count?:number}[]=[];
const repositories=await adapter.connect(owner);
for(const repository of repositories){for(const [operation,args] of [['repository_summary',{}],['repository_search',{query:'README',mode:'code',page:1}],...(repository.capabilities?.includes('history')?[['repository_history',{}]]:[])] as [string,Record<string,unknown>][]){try{const card=await adapter.read(owner,repository.id,operation,args);checks.push({operation,passed:true,count:card.evidence?.length??0});}catch(e){checks.push({operation,passed:false,error:(e as {code?:string}).code??'UNEXPECTED'});}}}
await mkdir('work/m4',{recursive:true});const result={at:new Date().toISOString(),repositoryCount:repositories.length,checks,live:true,note:'No repository contents or credentials retained. This smoke check does not establish the20-question semantic acceptance gate.'};await writeFile('work/m4/live-smoke.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));if(checks.some(c=>!c.passed))process.exitCode=1;
