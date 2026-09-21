import {it,expect} from 'vitest';
import {generateKeyPairSync,randomBytes,sign} from 'node:crypto';
import {mkdtempSync,writeFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {createApp} from '../../apps/server/src/app.js';
import {config} from '../../apps/server/src/config.js';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
import {FixtureTextProvider} from '../../packages/runtime/src/provider.js';
import {challengeMessage,redemptionMessage} from '../../packages/enrollment/src/service.js';
it('browser setup requires session/origin/CSRF; redemption accepts only the signed device proof',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'marvin-enrollment-api-')),pair=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),file=join(dir,'keys.json');writeFileSync(file,JSON.stringify({privateKey:pair.privateKey.export({type:'pkcs8',format:'pem'}),receiptKey:randomBytes(32).toString('hex')}));
 const service=await createApp(config({NODE_ENV:'test',ENROLLMENT_KEYS_FILE:file,DEVICE_PUBLIC_ORIGIN:'https://marvin.example'}),{database:sqliteDatabase(),provider:new FixtureTextProvider(0)});
 try{const owner=await service.store.ensureOwner('test','enrollment-api','Owner'),session=await service.store.createSession(owner.id),device=generateKeyPairSync('ec',{namedCurve:'prime256v1'}),id=await service.enrollment!.registerDevice(device.publicKey.export({type:'spki',format:'pem'}).toString());
 const challenge={deviceId:id,nonce:randomBytes(32).toString('hex'),issuedAt:Date.now(),operation:'claim' as const};const proof=(text:string)=>sign('sha256',Buffer.from(text),{key:device.privateKey,dsaEncoding:'ieee-p1363'}).toString('base64url');const payload={challenge,proof:proof(challengeMessage(challenge))};const headers={origin:'http://127.0.0.1:5173',cookie:'marvin_session='+session.token,'x-csrf-token':session.csrf};
 expect((await service.app.inject({method:'POST',url:'/api/robot/enrollment',headers:{origin:headers.origin},payload})).statusCode).toBe(401);
 expect((await service.app.inject({method:'POST',url:'/api/robot/enrollment',headers:{...headers,'x-csrf-token':'bad'},payload})).statusCode).toBe(403);
 const issued=await service.app.inject({method:'POST',url:'/api/robot/enrollment',headers,payload});expect(issued.statusCode).toBe(200);const ticket=issued.json().ticket,redemption={ticket,network:'Home',proof:proof(redemptionMessage(ticket,'Home'))};
 expect((await service.app.inject({method:'POST',url:'/api/device/enrollment/redeem',headers,payload:redemption})).statusCode).toBe(403);
 const result=await service.app.inject({method:'POST',url:'/api/device/enrollment/redeem',payload:redemption});expect(result.statusCode).toBe(200);expect(result.json().linked).toBe(true);expect(result.json().credential).toHaveLength(43);
 expect((await service.app.inject({method:'POST',url:'/api/device/enrollment/redeem',payload:{...redemption,password:'must-never-reach-server'}})).statusCode).toBe(400);
 }finally{await service.app.close();rmSync(dir,{recursive:true,force:true});}
});
