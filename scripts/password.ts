import { hashPassword } from '../apps/server/src/auth.js';
import { createInterface } from 'node:readline';
if(!process.stdin.isTTY)throw new Error('Run in an interactive terminal; passwords must not be command-line arguments.');
process.stdout.write('New local passphrase (12+ characters; input hidden): ');
process.stdin.setRawMode(true);let value='';
process.stdin.on('data',(chunk:Buffer)=>{for(const c of chunk.toString()){if(c==='\u0003')process.exit(1);if(c==='\r'||c==='\n'){process.stdin.setRawMode(false);process.stdin.pause();void hashPassword(value).then(hash=>{console.log('\nLOCAL_PASSWORD_HASH='+hash);}).catch(e=>{console.error('\n'+e.message);process.exitCode=1;});return;}if(c==='\u007f')value=value.slice(0,-1);else value+=c;}});
