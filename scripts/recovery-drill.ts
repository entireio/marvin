/** Measure a fresh synthetic SQLite + configuration recovery. Does not open the user's database or .env. */
import {mkdir,writeFile,readFile,mkdtemp,rm,stat} from 'node:fs/promises';
import {join} from 'node:path';
import {randomBytes,randomUUID,createHash} from 'node:crypto';
import {createApp} from '../apps/server/src/app.js';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
import {FixtureTextProvider} from '../packages/runtime/src/provider.js';
import {createBackup,restoreBackup} from '../packages/operations/src/backup.js';
const directory='work/m11/recovery';await mkdir(directory,{recursive:true});const work=await mkdtemp(join(directory,'fixture-')),source=join(work,'source.sqlite'),key=randomBytes(32),configuration=join(work,'configuration.env'),configurationText='FIXTURE_SECRET=synthetic-recovery-fixture\n';
let live:Awaited<ReturnType<typeof createApp>>|undefined,restored:Awaited<ReturnType<typeof createApp>>|undefined;
const digest=(data:unknown)=>createHash('sha256').update(JSON.stringify(data)).digest('hex');
try{
 live=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(source),provider:new FixtureTextProvider(0)});
 const owner=(await live.store.ensureOwner('test','recovery-drill','Synthetic owner')).id;
 for(let i=0;i<100;i++){const conversation=await live.store.createConversation(owner);for(let n=0;n<10;n++){await live.runtime.start(await live.runtime.context(owner,conversation.id,randomUUID(),'fixture-route'),`Synthetic conversation ${i}, turn ${n}.`);await live.runtime.drain();}}
 const snapshot=async(app:NonNullable<typeof live>)=>Promise.all(['owners','conversations','turns','events','tool_invocations'].map(table=>app.store.db.query(`SELECT * FROM ${table} ORDER BY 1,2`)));
 const before=await snapshot(live),expected=digest(before),databaseBytes=(await stat(source)).size;
 await writeFile(configuration,configurationText,{mode:0o600});const archive=join(work,'backup.enc'),backupStart=performance.now();const backup=await createBackup(source,key,archive,configuration),backupMs=performance.now()-backupStart;
 await live.app.close();live=undefined;
 const restoreStart=performance.now(),destination=join(work,'restored');restoreBackup(archive,key,destination);
 restored=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(join(destination,'marvin.sqlite')),provider:new FixtureTextProvider(0)});
 const health=await restored.app.inject({url:'/api/health'}),actual=digest(await snapshot(restored)),configMatches=await readFile(join(destination,'configuration.env'),'utf8')===configurationText;
 const restoreMs=performance.now()-restoreStart,passed=health.statusCode===200&&actual===expected&&configMatches;
 const report={at:new Date().toISOString(),passed,fixtureConversations:100,fixtureTurns:1000,databaseBytes,archiveBytes:backup.bytes,backupMs:Math.round(backupMs),restoreAndVerifyMs:Math.round(restoreMs),canonicalRowsMatch:actual===expected,configurationMatches:configMatches,healthStatus:health.statusCode,scope:'Synthetic SQLite snapshot and configuration restored into a fresh directory, application restarted, canonical owners/conversations/turns/events/tool records compared. Excludes production traffic switching, PostgreSQL recovery, off-machine retrieval, deletion reconciliation and backup scheduling. Does not establish a deployed RPO.',rtoTargetMs:1800000,localFixtureWithinTarget:passed&&restoreMs<=1800000};
 await writeFile(join(directory,'results.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));if(!passed)throw new Error('Recovery mismatch');
}finally{await live?.app.close();await restored?.app.close();key.fill(0);await rm(work,{recursive:true,force:true});}
