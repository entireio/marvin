import {randomBytes} from 'node:crypto';
import {chmodSync,mkdtempSync,readFileSync,rmSync,writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {describe,expect,it} from 'vitest';
import {EntireHostedCli,EntireSecretStore} from '../../packages/runtime/src/entire-hosted.js';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {migrate} from '../../packages/persistence/src/migrations.js';
import {Store} from '../../packages/persistence/src/store.js';

describe('hosted Entire credential storage',()=>{
 it('encrypts credentials at rest, round-trips them, and deletes them',async()=>{const directory=mkdtempSync(join(tmpdir(),'marvin-entire-secret-'));try{const store=new EntireSecretStore(directory,randomBytes(32).toString('base64')),bundle={tokens:'fixture-access-token',contexts:'{"current":"fixture"}'},ref=await store.put('owner-1',bundle),path=join(directory,ref+'.enc');expect(readFileSync(path,'utf8')).not.toContain(bundle.tokens);await expect(store.get(ref)).resolves.toEqual(bundle);await store.delete(ref);await expect(store.get(ref)).rejects.toMatchObject({code:'ENTIRE_REAUTH_REQUIRED'});}finally{rmSync(directory,{recursive:true,force:true});}});
 it('completes device login without exposing the token and verifies a cloud repository',async()=>{const directory=mkdtempSync(join(tmpdir(),'marvin-entire-login-test-')),executable=join(directory,'entire-fixture'),secretDirectory=join(directory,'secrets'),db=sqliteDatabase();writeFileSync(executable,`#!/usr/bin/env node
import fs from 'node:fs';import path from 'node:path';
const args=process.argv.slice(2);
if(args[0]==='login'){console.log('Device code: TEST-CODE');console.log('Login URL:');console.log('https://entire.io/device');await new Promise(r=>setTimeout(r,60));fs.writeFileSync(process.env.ENTIRE_TOKEN_STORE_PATH,JSON.stringify({token:'never-in-browser'}));fs.mkdirSync(process.env.ENTIRE_CONFIG_DIR,{recursive:true});fs.writeFileSync(path.join(process.env.ENTIRE_CONFIG_DIR,'contexts.json'),JSON.stringify({current:'test'}));console.log('Login complete.');}
else if(args[0]==='version')console.log('Entire CLI 0.11.0');
else if(args[0]==='repo')console.log(JSON.stringify({items:[{repo:'/gh/acme/widget',status:'ready'}]}));
else process.exit(2);
`);chmodSync(executable,0o700);try{await migrate(db);const persistence=new Store(db),owner=await persistence.ensureOwner('test','hosted-entire','Owner'),hosted=new EntireHostedCli(executable,new EntireSecretStore(secretDirectory,randomBytes(32).toString('base64')),persistence),attempt=await hosted.startLogin(owner.id);expect(attempt.userCode).toBe('TEST-CODE');expect(attempt.verificationUri).toBe('https://entire.io/device');let status=hosted.loginStatus(owner.id,attempt.id);for(let i=0;i<40&&status.status!=='connected';i++){await new Promise(resolve=>setTimeout(resolve,25));status=hosted.loginStatus(owner.id,attempt.id);}expect(status.status).toBe('connected');await expect(hosted.connect(owner.id)).resolves.toMatchObject([{name:'acme/widget'}]);expect((await persistence.owner(owner.id)).entireState).toBe('disconnected');await persistence.setEntireState(owner.id,'connected');await hosted.disconnect(owner.id);expect(await persistence.entireConnection(owner.id)).toBeNull();}finally{await db.close();rmSync(directory,{recursive:true,force:true});}});
});
