import { spawn } from 'node:child_process';
const children=[spawn('node',['--import','tsx','apps/server/src/main.ts'],{stdio:'inherit',env:process.env}),spawn('node',['node_modules/vite/bin/vite.js','--config','apps/web/vite.config.ts'],{stdio:'inherit',env:process.env})];
let stopping=false;function stop(){if(stopping)return;stopping=true;for(const c of children)c.kill('SIGTERM');}
for(const c of children)c.on('exit',code=>{stop();process.exitCode=code??0;});
for(const s of ['SIGINT','SIGTERM'] as const)process.on(s,stop);
