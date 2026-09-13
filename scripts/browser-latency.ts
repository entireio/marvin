/** Billable, isolated browser measurement; never uses the user's conversations. */
import {parseArgs} from 'node:util';
import {mkdir,mkdtemp,rm,writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {chromium} from 'playwright';
import {createApp} from '../apps/server/src/app.js';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
try{process.loadEnvFile('.env');}catch{}
const {values}=parseArgs({options:{samples:{type:'string',default:'100'}}}),count=Number(values.samples);
if(!Number.isInteger(count)||count<1||count>100)throw new Error('Use 1–100 samples; each submits one billable prompt.');
if(!process.env.OPENAI_API_KEY||!process.env.OPENAI_MODEL)throw new Error('Configure the key and model in .env.');
const directory='work/m3/browser-latency';await mkdir(directory,{recursive:true});const temporary=await mkdtemp(join(directory,'fixture-')),origin='http://127.0.0.1:4821',runId=Date.now();
const cfg=config({NODE_ENV:'test',PORT:'4821',APP_ORIGIN:origin,MODEL_PROVIDER:'openai',OPENAI_API_KEY:process.env.OPENAI_API_KEY,OPENAI_MODEL:process.env.OPENAI_MODEL});
const service=await createApp(cfg,{database:sqliteDatabase(join(temporary,'marvin.sqlite'))});let browser:Awaited<ReturnType<typeof chromium.launch>>|undefined,version='',fatal:string|null=null;const samples:{sample:number;passed:boolean;firstVisibleMs:number|null}[]=[];const httpErrors:{status:number;path:string;sample:number}[]=[];
try{
 await service.app.listen({host:'127.0.0.1',port:4821});browser=await chromium.launch({headless:true,channel:'chrome'});version=browser.version();const context=await browser.newContext({viewport:{width:1440,height:1000}}),owner=(await service.store.ensureOwner('test','browser-latency','Synthetic test')).id,session=await service.store.createSession(owner);
 await context.addCookies([{name:'marvin_session',value:session.token,url:origin,httpOnly:true,sameSite:'Lax'}]);
 await context.addInitScript(()=>{
  const state={submitted:0,first:0,pending:false};(window as any).__marvinLatency=state;
  document.addEventListener('submit',event=>{if((event.target as Element)?.matches('.composer')){state.submitted=performance.now();state.first=0;state.pending=false;}},true);
  new MutationObserver(()=>{if(!state.submitted||state.first||state.pending)return;const element=document.querySelector('.assistant-message .markdown p');if(!element?.textContent?.trim())return;state.pending=true;requestAnimationFrame(()=>requestAnimationFrame(()=>{state.first=performance.now()-state.submitted;}));}).observe(document,{childList:true,subtree:true,characterData:true});
 });
 const page=await context.newPage();page.on('response',response=>{if(response.status()>=400)httpErrors.push({status:response.status(),path:new URL(response.url()).pathname,sample:samples.length+1});});let subscribed=false;page.on('websocket',ws=>ws.on('framereceived',({payload})=>{try{if(JSON.parse(payload.toString()).type==='subscribed')subscribed=true;}catch{}}));
 for(let n=0;n<count;n++){
  const conversation=await service.store.createConversation(owner);subscribed=false;await page.goto(origin+'/app/'+conversation.id);await page.getByLabel('Message Marvin').waitFor();for(let i=0;i<500&&!subscribed;i++)await new Promise(r=>setTimeout(r,20));if(!subscribed)throw new Error('Subscription failed before measurement');
  await page.getByLabel('Message Marvin').fill('Reply with exactly: Ready.');let passed=false,firstVisibleMs:number|null=null;
  try{await page.getByRole('button',{name:'Send message'}).click();await page.waitForFunction(()=>(window as any).__marvinLatency.first>0,{},{timeout:60000});firstVisibleMs=await page.evaluate(()=>(window as any).__marvinLatency.first);await page.getByRole('button',{name:'Stop response'}).waitFor({state:'hidden',timeout:60000});const turns=await service.store.turns(owner,conversation.id);passed=turns.length===1&&turns[0].status==='completed'&&!!turns[0].assistantText;}catch{/* Preserve failed observations without recording provider payloads. */}
  samples.push({sample:n+1,passed,firstVisibleMs});await report('running');console.log(JSON.stringify(samples.at(-1)));if(samples.slice(-3).length===3&&samples.slice(-3).every(s=>!s.passed))throw new Error('Three consecutive failed observations; inspect service configuration');await new Promise(r=>setTimeout(r,5000));
 }
}catch(error){fatal=error instanceof Error?error.message.split('\n')[0]:'Browser measurement failed';}
finally{await browser?.close();await service.app.close();await rm(temporary,{recursive:true,force:true});await report(fatal?'failed':'finished');if(fatal||samples.some(s=>!s.passed))process.exitCode=1;}
async function report(status:string){const durations=samples.filter(s=>s.firstVisibleMs!==null).map(s=>s.firstVisibleMs!).sort((a,b)=>a-b),p95=durations[Math.ceil(durations.length*.95)-1]??null;const result={at:new Date().toISOString(),runId,status,model:cfg.OPENAI_MODEL,browser:'Chrome '+version,platform:process.platform,node:process.version,viewport:{width:1440,height:1000},targetSamples:count,samples,failures:samples.filter(s=>!s.passed).length,httpErrors,fatal,p50Ms:durations[Math.ceil(durations.length*.5)-1]??null,p95Ms:p95,measurement:'Composer submit event to two animation frames after the first assistant paragraph appears. Real model through Marvin HTTP/WebSocket/UI, fresh isolated conversation per request. Headless Chrome on developer host; other local soak processes may be running. Not a physical display or controlled-network baseline.',acceptance:status==='finished'&&count===100&&samples.length===100&&samples.every(s=>s.passed)&&p95!==null&&p95<=3000?'100-observation development rig target met; baseline environment remains to be certified':'not accepted'};await writeFile(`${directory}/run-${runId}.json`,JSON.stringify(result,null,2));if(status!=='running')console.log(JSON.stringify({...result,samples:undefined}));}
