import {DatabaseSync,backup} from 'node:sqlite';
import {createCipheriv,createDecipheriv,randomBytes,createHash} from 'node:crypto';
import {readFileSync,writeFileSync,mkdtempSync,rmSync,mkdirSync,existsSync,statSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {tmpdir} from 'node:os';
import {gzipSync,gunzipSync} from 'node:zlib';
import {z} from 'zod';
const MAGIC=Buffer.from('MARVIN-BACKUP-1\n');
const Payload=z.object({version:z.literal(1),createdAt:z.string(),database:z.string(),databaseSha256:z.string(),configuration:z.string().nullable()}).strict();
const digest=(b:Buffer)=>createHash('sha256').update(b).digest('hex');
export async function createBackup(database:string,key:Buffer,output:string,configuration?:string){
 if(key.length!==32)throw new Error('Backup key must contain 32 bytes.');if(existsSync(output))throw new Error('Backup output already exists.');
 const temp=mkdtempSync(join(tmpdir(),'marvin-backup-'));let db:DatabaseSync|undefined;
 try{db=new DatabaseSync(resolve(database),{readOnly:true});const snapshot=join(temp,'snapshot.sqlite');await backup(db,snapshot);if(statSync(snapshot).size>256*1024*1024)throw new Error('Use the operator large-database backup procedure above 256 MB.');
  const content=readFileSync(snapshot),config=configuration?readFileSync(configuration):null;if(config&&config.length>1024*1024)throw new Error('Configuration backup is too large.');
  const packed=gzipSync(JSON.stringify({version:1,createdAt:new Date().toISOString(),database:content.toString('base64'),databaseSha256:digest(content),configuration:config?.toString('base64')??null}));
  const nonce=randomBytes(12),cipher=createCipheriv('aes-256-gcm',key,nonce);cipher.setAAD(MAGIC);const encrypted=Buffer.concat([cipher.update(packed),cipher.final()]);writeFileSync(output,Buffer.concat([MAGIC,nonce,cipher.getAuthTag(),encrypted]),{flag:'wx',mode:0o600});return {bytes:statSync(output).size,databaseSha256:digest(content)};
 }finally{db?.close();rmSync(temp,{recursive:true,force:true});}
}
export function restoreBackup(archive:string,key:Buffer,directory:string){
 if(key.length!==32)throw new Error('Backup key must contain 32 bytes.');if(existsSync(directory))throw new Error('Restore into a new directory; existing data is never overwritten.');if(statSync(archive).size>384*1024*1024)throw new Error('Archive exceeds the supported restore size.');
 const data=readFileSync(archive);if(!data.subarray(0,MAGIC.length).equals(MAGIC))throw new Error('Unsupported backup format.');const at=MAGIC.length,decipher=createDecipheriv('aes-256-gcm',key,data.subarray(at,at+12));decipher.setAAD(MAGIC);decipher.setAuthTag(data.subarray(at+12,at+28));
 const packed=Buffer.concat([decipher.update(data.subarray(at+28)),decipher.final()]);const payload=Payload.parse(JSON.parse(gunzipSync(packed,{maxOutputLength:360*1024*1024}).toString()));const database=Buffer.from(payload.database,'base64');if(digest(database)!==payload.databaseSha256)throw new Error('Database checksum mismatch.');
 mkdirSync(directory,{mode:0o700});try{const path=join(directory,'marvin.sqlite');writeFileSync(path,database,{flag:'wx',mode:0o600});const db=new DatabaseSync(path,{readOnly:true});try{const check=db.prepare('PRAGMA integrity_check').get() as {integrity_check:string};if(check.integrity_check!=='ok')throw new Error('Database integrity check failed.');}finally{db.close();}if(payload.configuration!==null)writeFileSync(join(directory,'configuration.env'),Buffer.from(payload.configuration,'base64'),{flag:'wx',mode:0o600});return {createdAt:payload.createdAt,databaseSha256:payload.databaseSha256};}catch(e){rmSync(directory,{recursive:true,force:true});throw e;}
}
