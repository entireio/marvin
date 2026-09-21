import { existsSync } from 'node:fs';
import { loadEnvFile } from 'node:process';
import { config } from './config.js';
import { createApp } from './app.js';
if(existsSync('.env'))loadEnvFile('.env');
const cfg=config();const {app}=await createApp(cfg,{logger:true});
await app.listen({host:cfg.HOST,port:cfg.PORT});
console.log(`Marvin API: http://${cfg.HOST}:${cfg.PORT}; auth=${cfg.AUTH_MODE}; provider=${cfg.MODEL_PROVIDER}`);
for(const sig of ['SIGTERM','SIGINT'] as const)process.on(sig,()=>{void app.close().then(()=>process.exit(0));});
