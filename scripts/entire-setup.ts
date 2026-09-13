import { parseArgs } from 'node:util';
import { homedir } from 'node:os';
import { resolve,join } from 'node:path';
import { mkdir,readFile,writeFile,rename,chmod } from 'node:fs/promises';
import { createInterface } from 'node:readline/promises';
import { stdin,stdout } from 'node:process';
import { config } from '../apps/server/src/config.js';
import { sqliteDatabase } from '../packages/persistence/src/database.js';
import { migrate } from '../packages/persistence/src/migrations.js';
import { Store } from '../packages/persistence/src/store.js';
import { EntireCli,Bindings,runCommand } from '../packages/runtime/src/entire-cli.js';
try{process.loadEnvFile('.env');}catch{}
const {values}=parseArgs({options:{repo:{type:'string'},context:{type:'string'},checkout:{type:'string'},cli:{type:'string'},kind:{type:'string'},check:{type:'boolean'}}});
const cfg=config();if(cfg.AUTH_MODE==='oidc'||cfg.DATABASE_URL)throw new Error('This setup command is for the local SQLite deployment. Hosted Entire authorization is a separate integration.');
const executable=values.cli??cfg.ENTIRE_CLI_PATH??join(homedir(),'.local/bin/entire');
const readline=createInterface({input:stdin,output:stdout});
try{
 const context=values.context??await readline.question('Entire login context (from entire auth contexts): ');
 const env={PATH:process.env.PATH,HOME:process.env.HOME,TMPDIR:process.env.TMPDIR,ENTIRE_TELEMETRY_OPTOUT:'1',GIT_TERMINAL_PROMPT:'0'};
 console.log((await runCommand(executable,['version'],{env})).split('\n')[0]);
 let name=values.repo;
 if(!name){const raw=await runCommand(executable,['--context',context,'repo','mirror','list','--mirrored','--json','--page-size','25'],{env});const page=JSON.parse(raw) as {items:{repo:string}[];nextPageToken?:string};for(const item of page.items??[])console.log(item.repo);if(page.nextPageToken)console.log('More repositories are available; enter an exact name to select one.');name=await readline.question('Repository owner/name (or project/name for native): ');}
 const checkout=values.checkout??(stdin.isTTY?await readline.question('Optional local checkout root (Enter to skip): '):'');
 const db=sqliteDatabase(cfg.SQLITE_PATH);await migrate(db);const store=new Store(db);let owner;
 try{owner=await store.ensureOwner(cfg.AUTH_MODE==='local'?'local':'development','owner',cfg.AUTH_MODE==='local'?'Marvin owner':'Local developer');}finally{await db.close();}
 const destination=resolve('data/entire-bindings.json'),temporary=destination+'.pending';await mkdir('data',{recursive:true,mode:0o700});
 let bindings:zBindings=[];try{bindings=Bindings.parse(JSON.parse(await readFile(destination,'utf8')));}catch(e){if((e as NodeJS.ErrnoException).code!=='ENOENT')throw e;}
 const binding=Bindings.parse([{ownerId:owner.id,context,repositories:[{name,kind:values.kind??'mirror',...(checkout?{checkout:resolve(checkout)}:{})}]}])[0];
 await writeFile(temporary,JSON.stringify([...bindings.filter(b=>b.ownerId!==owner.id),binding],null,2),{mode:0o600});await chmod(temporary,0o600);
 const integration=new EntireCli(executable,temporary);const repos=await integration.connect(owner.id);console.log(`Access verified for ${repos[0].name}. No capture hooks installed.`);
 if(values.check){console.log('Check only: current configuration unchanged.');}
 else{await rename(temporary,destination);console.log(`Add these paths to .env, restart Marvin, then choose Settings → Connections → Connect Entire:\nENTIRE_CLI_PATH=${executable}\nENTIRE_BINDINGS_FILE=${destination}`);}
}finally{readline.close();}
type zBindings=Awaited<ReturnType<EntireCli['binding']>>[];
