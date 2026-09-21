import {it,expect} from 'vitest';
import {createApp} from '../../apps/server/src/app.js';
import {config} from '../../apps/server/src/config.js';
import {sqliteDatabase} from '../../packages/persistence/src/database.js';
it('page loads do not consume API limits and repeated login attempts remain limited',async()=>{
 const service=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase()});
 try{
  for(let i=0;i<210;i++)expect((await service.app.inject({url:'/login'})).statusCode).not.toBe(429);
  let last=0;
  for(let i=0;i<11;i++)last=(await service.app.inject({method:'POST',url:'/api/auth/login',headers:{origin:'http://127.0.0.1:5173'},payload:{}})).statusCode;
  expect(last).toBe(429);
  expect((await service.app.inject({url:'/login'})).statusCode).not.toBe(429);
 }finally{await service.app.close();}
});
for(const hops of [0,1])it(`forwarded addresses affect rate limits only with an explicit trusted proxy: ${hops}`,async()=>{
 const service=await createApp(config({NODE_ENV:'test',AUTH_MODE:'local',LOCAL_PASSWORD_HASH:'scrypt$'+'0'.repeat(32)+'$'+'0'.repeat(128),TRUST_PROXY_HOPS:String(hops)}),{database:sqliteDatabase()});
 try{let limited=0;for(let i=0;i<220;i++){const response=await service.app.inject({url:'/api/health',remoteAddress:'10.0.0.2',headers:{'x-forwarded-for':i<110?'192.0.2.1':'192.0.2.2'}});if(response.statusCode===429)limited++;}expect(limited).toBe(hops?0:20);}finally{await service.app.close();}
});
it('development login refuses trusted forwarded addresses',()=>{expect(()=>config({TRUST_PROXY_HOPS:'1'})).toThrow('must not trust');});
