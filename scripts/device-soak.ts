import { parseArgs } from 'node:util';
import { mkdir,writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { createApp } from '../apps/server/src/app.js';
import { config } from '../apps/server/src/config.js';
import { sqliteDatabase } from '../packages/persistence/src/database.js';
import { FixtureTextProvider } from '../packages/runtime/src/provider.js';
import { DeviceSimulator } from '../packages/device/src/simulator.js';
import type { InteractionContext } from '../packages/contracts/src/index.js';
const {values}=parseArgs({options:{seconds:{type:'string',default:'86400'},devices:{type:'string',default:'10'},output:{type:'string',default:'work/m6/soak'}}});
const seconds=Number(values.seconds),count=Number(values.devices);if(!Number.isInteger(seconds)||seconds<60||seconds>86400||!Number.isInteger(count)||count<1||count>100)throw new Error('Use 60–86400 seconds and 1–100 devices.');
const directory=values.output!;await mkdir(directory,{recursive:true});const runId=randomUUID(),started=Date.now();const service=await createApp(config({NODE_ENV:'test'}),{database:sqliteDatabase(`${directory}/${runId}.sqlite`),provider:new FixtureTextProvider(0)});
const url=(await service.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:')+'/api/device/socket';const robots:{sim:DeviceSimulator;ctx:InteractionContext;token:string}[]=[];let stopping=false,actions=0,lastTick=Date.now(),failure:string|undefined,baseline:number|undefined,peak=0;const samples:{elapsedSeconds:number;rssBytes:number;heapBytes:number;connections:number;actions:number}[]=[];
for(const sig of ['SIGTERM','SIGINT'] as const)process.on(sig,()=>{stopping=true;failure='INTERRUPTED';});
try{
 for(let n=0;n<count;n++){const owner=(await service.store.ensureOwner('soak',String(n),'Simulator')).id,device=randomUUID(),enrollment=randomUUID();await service.store.reserve(owner,device,enrollment,true);await service.store.redeem(owner,device,enrollment,'Simulated network');const token=await service.devices.persistence.issue(owner,device),conversation=await service.store.createConversation(owner),sim=new DeviceSimulator(device);await sim.connect(url,token);robots.push({sim,token,ctx:{...await service.runtime.context(owner,conversation.id,randomUUID(),device),surface:'body_voice',body:{deviceId:device,status:'online',capabilities:['eyes']}}});}
 for(let tick=0;!stopping&&Date.now()-started<seconds*1000;tick++){
  const now=Date.now();if(now-lastTick>30000)throw new Error('CLOCK_OR_SLEEP_GAP');lastTick=now;
  if(tick%60===0){for(const robot of robots){await service.devices.dispatch(robot.ctx,'eyes',{expression:'neutral'},randomUUID());actions++;}await service.devices.persistence.expire();}
  if(tick%60===30){const r=robots[(Math.floor(tick/60))%robots.length];r.sim.disconnect();await delay(20);await r.sim.connect(url,r.token);}
  if(tick%10===0){const m=process.memoryUsage(),elapsedSeconds=Math.floor((now-started)/1000);if(elapsedSeconds>=300)baseline??=m.rss;if(baseline)peak=Math.max(peak,m.rss);samples.push({elapsedSeconds,rssBytes:m.rss,heapBytes:m.heapUsed,connections:robots.filter(r=>service.devices.presence(r.ctx.ownerId)).length,actions});if(samples.length>10000)samples.shift();await report('running');}
  await delay(1000);
 }
}catch(e){failure=e instanceof Error?e.message:'SOAK_FAILED';}
finally{for(const r of robots)r.sim.close();await service.app.close();await report(failure?'failed':'finished');}
async function report(status:string){const elapsedSeconds=Math.floor((Date.now()-started)/1000),growth=baseline?(peak-baseline)/baseline:null;const report={runId,pid:process.pid,startedAt:new Date(started).toISOString(),status,elapsedSeconds,targetSeconds:seconds,devices:count,actions,failures:failure?[failure]:[],providerSessions:0,uploadedAudioBytes:robots.reduce((n,r)=>n+r.sim.uploadedAudioBytes,0),baselineRssBytes:baseline??null,peakRssBytesAfterWarmup:peak||null,growthRatio:growth,acceptance:status==='finished'&&seconds===86400&&elapsedSeconds>=86400&&growth!==null&&growth<.1?'passed':'not accepted',workload:'10-second measurements; one eye action per device per minute; one reconnect per minute; five-second heartbeat; no model/audio sessions; five-minute warm-up',samples};await writeFile(`${directory}/results.json`,JSON.stringify(report,null,2));if(status!=='running')console.log(JSON.stringify({...report,samples:undefined}));}
