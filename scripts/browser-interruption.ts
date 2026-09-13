/** No provider calls: measure local worklet interruption with generated input and queued playback. */
import {mkdir,writeFile} from 'node:fs/promises';
import {chromium} from 'playwright';
import {createApp} from '../apps/server/src/app.js';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
const origin='http://127.0.0.1:4823',directory='work/m5/interruption-'+Date.now();await mkdir(directory,{recursive:true});
const service=await createApp(config({NODE_ENV:'test',APP_ORIGIN:origin}),{database:sqliteDatabase()});let browser:Awaited<ReturnType<typeof chromium.launch>>|undefined,fatal:string|null=null;const samples:{sample:number;passed:boolean;onsetToStopMs:number}[]=[];
try{
 await service.app.listen({host:'127.0.0.1',port:4823});browser=await chromium.launch({channel:'chrome',headless:true});const context=await browser.newContext(),owner=await service.store.ensureOwner('test','interruption','Synthetic test'),session=await service.store.createSession(owner.id),conversation=await service.store.createConversation(owner.id);
 await context.addCookies([{name:'marvin_session',value:session.token,url:origin,httpOnly:true,sameSite:'Lax'}]);
 await context.addInitScript({content:`
 window.audit={onset:0,stop:0,starts:0};
 const nativeStop=AudioBufferSourceNode.prototype.stop,nativeStart=AudioBufferSourceNode.prototype.start;
 AudioBufferSourceNode.prototype.start=function(...args){if(!this.synthetic)window.audit.starts++;return nativeStart.apply(this,args);};
 AudioBufferSourceNode.prototype.stop=function(...args){if(!this.synthetic&&!window.audit.stop)window.audit.stop=performance.now();return nativeStop.apply(this,args);};
 Object.defineProperty(navigator.mediaDevices,'getUserMedia',{value:async()=>{const ctx=new AudioContext({sampleRate:24000}),dest=ctx.createMediaStreamDestination();await ctx.resume();
 window.speak=()=>{window.audit.onset=performance.now();window.audit.stop=0;const buffer=ctx.createBuffer(1,4800,24000),data=buffer.getChannelData(0);for(let i=0;i<data.length;i++)data[i]=.1*Math.sin(2*Math.PI*440*i/24000);const s=ctx.createBufferSource();s.synthetic=true;s.buffer=buffer;s.connect(dest);s.start();};
 const track=dest.stream.getAudioTracks()[0],stop=track.stop.bind(track);track.stop=()=>{stop();ctx.close();};return dest.stream;}});
 `});
 const page=await context.newPage();await page.route('**/api/config',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),voiceAvailable:true}});});
 let send:(value:object)=>void=()=>{},interrupts=0;await page.routeWebSocket('**/api/voice',ws=>{send=value=>ws.send(JSON.stringify({v:1,...value}));ws.onMessage(raw=>{if(typeof raw!=='string')return;const e=JSON.parse(raw);if(e.type==='start'){send({type:'ready',sampleRate:24000,routeId:'synthetic'});send({type:'state',state:'listening'});}if(e.type==='interrupt')interrupts++;});});
 await page.goto(origin+'/app/'+conversation.id);await page.getByLabel('Talk to Marvin').click();await page.getByRole('button',{name:'Start voice',exact:true}).click();await page.getByRole('heading',{name:'I’m listening'}).waitFor();
 for(let n=0;n<100;n++){
  const before=await page.evaluate(()=>(window as any).audit.starts),count=interrupts;send({type:'turn',interactionId:'turn-'+n,text:'Synthetic input'});for(let k=0;k<4;k++)send({type:'audio',interactionId:'turn-'+n,pcm:Buffer.alloc(12000).toString('base64')});
  await page.waitForFunction(value=>(window as any).audit.starts>value,before);await page.evaluate(()=>(window as any).speak());await page.waitForFunction(()=>(window as any).audit.stop>0,{},{timeout:5000});
  const audit=await page.evaluate(()=>(window as any).audit);const result={sample:n+1,passed:audit.stop>=audit.onset&&interrupts>count,onsetToStopMs:audit.stop-audit.onset};samples.push(result);await report();console.log(JSON.stringify(result));await page.waitForTimeout(1600);
 }
 await page.getByRole('button',{name:'Continue in text',exact:true}).click();
}catch(e){fatal=e instanceof Error?e.message.split('\n')[0]:'Interruption test failed';process.exitCode=1;}finally{await browser?.close();await service.app.close();await report();}
async function report(){const timings=samples.map(s=>s.onsetToStopMs).sort((a,b)=>a-b);await writeFile(directory+'/results.json',JSON.stringify({at:new Date().toISOString(),samples,fatal,p50Ms:timings[Math.ceil(timings.length*.5)-1]??null,p95Ms:timings[Math.ceil(timings.length*.95)-1]??null,scope:'100 synthetic waveform onsets through actual capture AudioWorklet to AudioBufferSourceNode.stop. Browser provider socket is an explicit fixture. Includes local detector lag; excludes physical microphone/speaker and acoustic echo.'},null,2));}
