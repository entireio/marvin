import { OpenAITextProvider } from '../packages/runtime/src/provider.js';
import { performance } from 'node:perf_hooks';
import { writeFile,mkdir } from 'node:fs/promises';
try { process.loadEnvFile('.env'); } catch { /* Explicit process environment is also supported. */ }
if(!process.env.OPENAI_API_KEY||!process.env.OPENAI_MODEL)throw new Error('Set OPENAI_API_KEY and OPENAI_MODEL in .env or the environment.');
const provider=new OpenAITextProvider(process.env.OPENAI_API_KEY,process.env.OPENAI_MODEL);
const samples=Number(process.env.MARVIN_SMOKE_SAMPLES??1);
if(!Number.isInteger(samples)||samples<1||samples>100)throw new Error('MARVIN_SMOKE_SAMPLES must be 1–100. Each sample makes a billable request.');
const times:number[]=[], failures:string[]=[],diagnostics:{sample:number;status:number|null;code:string|null}[]=[];
for(let i=0;i<samples;i++){
 const start=performance.now();let first:number|undefined;
 try{for await(const e of provider.run({interaction:{ownerId:'smoke',conversationId:'smoke',interactionId:`smoke-${i}`,routeId:'smoke',surface:'web_text',repositoryId:null,entireState:'disconnected',body:null},input:'Reply with a brief greeting.',summary:'',messages:[]},async()=>{throw new Error('No tools allowed');},AbortSignal.timeout(60000))){if(e.type==='delta'&&first===undefined)first=performance.now()-start;}if(first===undefined)throw new Error('No visible text');times.push(first);}catch(error){failures.push(`sample-${i}`);const e=error as {status?:unknown;code?:unknown};diagnostics.push({sample:i,status:typeof e.status==='number'?e.status:null,code:typeof e.code==='string'&&/^[a-zA-Z0-9_-]{1,80}$/.test(e.code)?e.code:null});}
}
times.sort((a,b)=>a-b);
const result={provider:'openai',model:process.env.OPENAI_MODEL,samples,successes:times.length,failures,diagnostics,p50Ms:times[Math.ceil(times.length*.5)-1]??null,p95Ms:times[Math.ceil(times.length*.95)-1]??null,measurement:'Server adapter to first text delta; not browser end-to-end latency',at:new Date().toISOString()};
await mkdir('work/evidence',{recursive:true});await writeFile('work/evidence/provider-smoke.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));if(failures.length)process.exitCode=1;
