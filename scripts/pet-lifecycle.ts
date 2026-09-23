/** Private fleet inventory and recovery operator. Never prints setup secrets. */
import {createHash,createPublicKey,generateKeyPairSync} from 'node:crypto';
import {chmodSync,copyFileSync,existsSync,mkdirSync,readFileSync,readdirSync,writeFileSync} from 'node:fs';
import {dirname,join,resolve} from 'node:path';
import {parseArgs} from 'node:util';
import {z} from 'zod';
import {sqliteDatabase,postgresDatabase} from '../packages/persistence/src/database.js';
import {migrate} from '../packages/persistence/src/migrations.js';
import {Store} from '../packages/persistence/src/store.js';
import {recoveryPublicKey,signRecoveryTicket} from '../packages/enrollment/src/recovery.js';

process.umask(0o077);
const Device=z.object({schema:z.literal(1),label:z.string().min(1).max(80),mac:z.string().regex(/^[0-9a-f]{2}(?::[0-9a-f]{2}){5}$/),deviceId:z.string().regex(/^marvin_[a-f0-9]{32}$/),status:z.enum(['active','spare','service','retired']).default('spare'),currentDeployment:z.enum(['local','cloud','unassigned']).default('unassigned'),identityDirectory:z.string().min(1),setupCard:z.string().min(1),publicKey:z.string().min(1),lastBackup:z.string().optional()}).strict();
const {positionals,values}=parseArgs({allowPositionals:true,options:{vault:{type:'string'},device:{type:'string'},label:{type:'string'},mac:{type:'string'},identity:{type:'string'},backup:{type:'string'},status:{type:'string'},deployment:{type:'string'},operator:{type:'string'},reason:{type:'string'},target:{type:'string'},key:{type:'string'},challenge:{type:'string'},owner:{type:'string'},epoch:{type:'string'},case:{type:'string'},out:{type:'string'}}});
const operation=positionals[0],vault=values.vault?resolve(values.vault):'';
const fail=(message:string):never=>{throw new Error(message);};
const manifestPath=(name:string)=>join(vault,'devices',name,'device.json');
const load=(name:string)=>Device.parse(JSON.parse(readFileSync(manifestPath(name),'utf8')));
const deviceDirectories=()=>{const base=join(vault,'devices');if(!existsSync(base))return [];return readdirSync(base,{withFileTypes:true}).filter(x=>x.isDirectory()&&existsSync(join(base,x.name,'device.json'))).map(x=>x.name).sort();};
if(!operation)fail('Usage: pet-lifecycle.ts <import|set|derive|list|card|local-registry|operator-unlink|recovery-key|release-key|recovery-ticket> ...');

if(operation==='import'){
 if(!vault||!values.device||!values.label||!values.mac||!values.identity)fail('import requires --vault --device --label --mac --identity');
 const device=values.device!,label=values.label!,mac=values.mac!,identity=resolve(values.identity!),secret=JSON.parse(readFileSync(join(identity,'setup-secret.json'),'utf8')),publicKey=readFileSync(join(identity,'device-public.pem'),'utf8');
 const id=String(secret.deviceId),fingerprint='marvin_'+createHash('sha256').update(createPublicKey(publicKey).export({format:'der',type:'spki'})).digest('hex').slice(0,32);
 if(id!==fingerprint)fail('Setup card and device public key do not match.');
 for(const name of deviceDirectories()){const existing=load(name);if(existing.mac===mac.toLowerCase()||existing.deviceId===id)fail(`The physical Pet is already imported as ${name}.`);}
 const target=dirname(manifestPath(device)),canonicalIdentity=join(target,'identity');mkdirSync(canonicalIdentity,{recursive:true,mode:0o700});
 for(const filename of ['factory.csv','device-public.pem','setup-secret.json']){const source=join(identity,filename),destination=join(canonicalIdentity,filename);if(!existsSync(source))fail(`Identity is incomplete: ${filename} is missing.`);copyFileSync(source,destination);chmodSync(destination,0o600);}
 const optionalPrivate=join(identity,'device-private.pem');if(existsSync(optionalPrivate)){const destination=join(canonicalIdentity,'device-private.pem');copyFileSync(optionalPrivate,destination);chmodSync(destination,0o600);}
 const card=join(canonicalIdentity,'setup-card.txt'),sourceCard=join(identity,'setup-card.txt');if(existsSync(sourceCard)){copyFileSync(sourceCard,card);chmodSync(card,0o600);}else{const body={deviceId:id,username:secret.username,password:secret.password};writeFileSync(card,'marvin1.'+Buffer.from(JSON.stringify(body)).toString('base64url')+'\n',{mode:0o600,flag:'wx'});}
 const record=Device.parse({schema:1,label,mac:mac.toLowerCase(),deviceId:id,status:'spare',currentDeployment:'unassigned',identityDirectory:canonicalIdentity,setupCard:card,publicKey:join(canonicalIdentity,'device-public.pem'),...(values.backup?{lastBackup:resolve(values.backup)}:{})});
 writeFileSync(manifestPath(device),JSON.stringify(record,null,2)+'\n',{mode:0o600,flag:'wx'});console.log(`Imported ${device}: ${id}`);
}else if(operation==='set'){
 if(!vault||!values.device)fail('set requires --vault --device and at least one of --status, --deployment, or --backup');const name=values.device!,record=load(name);
 const updated=Device.parse({...record,...(values.status?{status:values.status}:{}),...(values.deployment?{currentDeployment:values.deployment}:{}),...(values.backup?{lastBackup:resolve(values.backup)}:{})});writeFileSync(manifestPath(name),JSON.stringify(updated,null,2)+'\n',{mode:0o600});console.log(`Updated ${name}.`);
}else if(operation==='derive'){
 if(!vault||!values.device||!values.target||!values.out||!['local','cloud'].includes(values.target))fail('derive requires --vault --device --target local|cloud --out DIRECTORY');const record=load(values.device!),destination=resolve(values.out!);mkdirSync(destination,{recursive:true,mode:0o700});
 if(readdirSync(destination).length)fail('Derived deployment directory must be empty.');for(const filename of readdirSync(record.identityDirectory)){const source=join(record.identityDirectory,filename);if(!existsSync(source))continue;const output=join(destination,filename);copyFileSync(source,output);chmodSync(output,0o600);}writeFileSync(join(destination,'deployment.json'),JSON.stringify({schema:1,device:values.device,target:values.target,deviceId:record.deviceId,mac:record.mac,derivedAt:new Date().toISOString()},null,2)+'\n',{mode:0o600,flag:'wx'});console.log(`Derived ${values.target} deployment identity for ${values.device}: ${destination}`);
}else if(operation==='list'){
 if(!vault)fail('list requires --vault');for(const name of deviceDirectories()){const d=load(name);console.log(`${name}\t${d.mac}\t${d.deviceId}\t${d.status}\t${d.currentDeployment}`);}
}else if(operation==='card'){
 if(!vault||!values.device)fail('card requires --vault --device');const d=load(values.device!);if(!existsSync(d.setupCard))fail('The canonical setup card is missing.');console.log(d.setupCard);
}else if(operation==='local-registry'){
 if(!vault||!values.out)fail('local-registry requires --vault --out');const pets=deviceDirectories().map(load).filter(d=>d.status!=='retired').map(d=>({label:d.label,cardFile:d.setupCard,devicePublicKeyFile:d.publicKey}));writeFileSync(resolve(values.out!),JSON.stringify(pets,null,2)+'\n',{mode:0o600,flag:'wx'});console.log(`Created private registry for ${pets.length} Pets.`);
}else if(operation==='operator-unlink'){
 if(!values.device||!values.operator||!values.reason)fail('operator-unlink requires --device --operator --reason');const db=process.env.DATABASE_URL?postgresDatabase(process.env.DATABASE_URL):sqliteDatabase(process.env.SQLITE_PATH??'./data/marvin.sqlite');
 try{await migrate(db);const result=await new Store(db).operatorUnlinkDevice(values.device!,values.operator!,values.reason!,values.target);console.log(JSON.stringify(result));}finally{await db.close();}
}else if(operation==='recovery-key'){
 if(!values.out)fail('recovery-key requires --out');const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),path=resolve(values.out!);mkdirSync(dirname(path),{recursive:true,mode:0o700});writeFileSync(path,pair.privateKey.export({format:'pem',type:'pkcs8'}),{mode:0o600,flag:'wx'});writeFileSync(path+'.pub',recoveryPublicKey(readFileSync(path,'utf8')),{mode:0o600,flag:'wx'});console.log(`Created dedicated fleet recovery key files at ${path} and ${path}.pub; import the private key into KMS before production use.`);
}else if(operation==='release-key'){
 if(!values.out)fail('release-key requires --out');const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),path=resolve(values.out!);mkdirSync(dirname(path),{recursive:true,mode:0o700});writeFileSync(path,pair.privateKey.export({format:'pem',type:'pkcs8'}),{mode:0o600,flag:'wx'});writeFileSync(path+'.pub',pair.publicKey.export({format:'pem',type:'spki'}),{mode:0o600,flag:'wx'});console.log(`Created dedicated firmware release key files at ${path} and ${path}.pub; import the private key into KMS before production use.`);
}else if(operation==='recovery-ticket'){
 if(!values.key||!values.challenge||!values.owner||!values.epoch||!values.operator||!values.case||!values.out)fail('recovery-ticket requires --key --challenge --owner --epoch --operator --case --out');
 const ticket=signRecoveryTicket(readFileSync(resolve(values.key!),'utf8'),{challenge:JSON.parse(readFileSync(resolve(values.challenge!),'utf8')),ownerId:values.owner!,epoch:Number(values.epoch!),operatorId:values.operator!,caseId:values.case!});writeFileSync(resolve(values.out!),ticket+'\n',{mode:0o600,flag:'wx'});console.log(`Created one-time recovery ticket: ${resolve(values.out!)}`);
}else fail(`Unknown operation: ${operation}`);
