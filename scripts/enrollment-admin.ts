import {generateKeyPairSync,randomBytes} from 'node:crypto';
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {sqliteDatabase,postgresDatabase} from '../packages/persistence/src/database.js';
import {migrate} from '../packages/persistence/src/migrations.js';
import {Store} from '../packages/persistence/src/store.js';
import {EnrollmentService} from '../packages/enrollment/src/service.js';
import {z} from 'zod';
process.umask(0o077);
const [operation,path]=process.argv.slice(2);
if(!path||!['keys','register'].includes(operation))throw new Error('Usage: enrollment-admin.ts keys /private/keys.json OR register /path/device-public.pem (with enrollment environment configured).');
if(operation==='keys'){
 const pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),output=resolve(path);mkdirSync(dirname(output),{recursive:true,mode:0o700});writeFileSync(output,JSON.stringify({privateKey:pair.privateKey.export({format:'pem',type:'pkcs8'}),receiptKey:randomBytes(32).toString('hex')}),{mode:0o600,flag:'wx'});console.log('Created deployment keys: '+output);
}else{
 const cfg=z.object({ENROLLMENT_KEYS_FILE:z.string(),DEVICE_PUBLIC_ORIGIN:z.url(),DATABASE_URL:z.string().optional(),SQLITE_PATH:z.string().default('./data/marvin.sqlite')}).parse(process.env);
 const db=cfg.DATABASE_URL?postgresDatabase(cfg.DATABASE_URL):sqliteDatabase(cfg.SQLITE_PATH);
 try{await migrate(db);const keys=z.object({privateKey:z.string(),receiptKey:z.string().regex(/^[a-f0-9]{64}$/)}).strict().parse(JSON.parse(readFileSync(cfg.ENROLLMENT_KEYS_FILE,'utf8')));const service=new EnrollmentService(new Store(db),keys.privateKey,Buffer.from(keys.receiptKey,'hex'),cfg.DEVICE_PUBLIC_ORIGIN);console.log('Registered device: '+await service.registerDevice(readFileSync(resolve(path),'utf8')));}finally{await db.close();}
}
