import {readFileSync} from 'node:fs';
import {parseArgs} from 'node:util';
import {createBackup,restoreBackup} from '../packages/operations/src/backup.js';
process.umask(0o077);
const {values}=parseArgs({options:{operation:{type:'string'},database:{type:'string'},archive:{type:'string'},output:{type:'string'},key:{type:'string'},configuration:{type:'string'}}});
if(!values.key||!values.output)throw new Error('Provide --key /private/backup-key.hex and --output /new/destination.');
const hex=readFileSync(values.key,'utf8').trim();if(!/^[a-fA-F0-9]{64}$/.test(hex))throw new Error('Key file must contain exactly 32 random bytes encoded as hex.');const key=Buffer.from(hex,'hex');
try{if(values.operation==='backup'&&values.database)console.log(await createBackup(values.database,key,values.output,values.configuration));else if(values.operation==='restore'&&values.archive)console.log(restoreBackup(values.archive,key,values.output));else throw new Error('Use --operation backup --database /path/db OR --operation restore --archive /path/backup.');}finally{key.fill(0);}
