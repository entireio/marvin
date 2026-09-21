/** Live browser capture/worklet/playback smoke with synthetic input; no microphone permission. */
import {readFile,mkdir,writeFile} from 'node:fs/promises';
import {parseArgs} from 'node:util';
import {chromium,type Page} from 'playwright';
import {createApp} from '../apps/server/src/app.js';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
try{process.loadEnvFile('.env');}catch{}
const {values}=parseArgs({options:{input:{type:'string'},samples:{type:'string',default:'1'}}}),count=Number(values.samples);if(!values.input||!Number.isInteger(count)||count<1||count>100)throw new Error('Supply --input synthetic mono24k PCM and --samples 1–100.');
const pcm=await readFile(values.input);if(pcm.length%2||!pcm.length||pcm.length>2880000)throw new Error('Invalid bounded synthetic PCM.');
const origin='http://127.0.0.1:4822',directory='work/m5/browser-voice-'+Date.now();await mkdir(directory,{recursive:true,mode:0o700});
const cfg=config({NODE_ENV:'test',APP_ORIGIN:origin,VOICE_PROVIDER:'openai',OPENAI_API_KEY:process.env.OPENAI_API_KEY,OPENAI_REALTIME_MODEL:process.env.OPENAI_REALTIME_MODEL});
const service=await createApp(cfg,{database:sqliteDatabase()});let browser:Awaited<ReturnType<typeof chromium.launch>>|undefined;let page:Page|undefined;const results:unknown[]=[];let fatal:string|null=null;
try{
 await service.app.listen({host:'127.0.0.1',port:4822});browser=await chromium.launch({channel:'chrome',headless:true});const context=await browser.newContext();
 const owner=await service.store.ensureOwner('test','browser-voice','Synthetic speech'),session=await service.store.createSession(owner.id);await context.addCookies([{name:'marvin_session',value:session.token,url:origin,httpOnly:true,sameSite:'Lax'}]);
 await context.addInitScript(({base64})=>{
  // tsx preserves nested function names with this helper; it is absent in a browser realm.
  Object.defineProperty(globalThis,'__name',{value:Function('fn','return fn'),configurable:true});
  const audit={captures:0,stops:0,playbackStarts:0,inputEnd:0,firstPlayback:0};(window as any).voiceAudit=audit;
  const start=AudioBufferSourceNode.prototype.start;
  AudioBufferSourceNode.prototype.start=function(...args:Parameters<typeof start>){if(!(this as any).syntheticInput){audit.playbackStarts++;audit.firstPlayback||=performance.now()+Math.max(0,(args[0]??this.context.currentTime)-this.context.currentTime)*1000;}return start.apply(this,args);};
  Object.defineProperty(navigator.mediaDevices,'getUserMedia',{value:async()=>{
   audit.captures++;const ctx=new AudioContext({sampleRate:24000}),destination=ctx.createMediaStreamDestination();await ctx.resume();
   (window as any).speakSynthetic=()=>{const raw=atob(base64),samples=new Float32Array(raw.length/2);for(let i=0;i<samples.length;i++){let n=raw.charCodeAt(i*2)|(raw.charCodeAt(i*2+1)<<8);if(n>=32768)n-=65536;samples[i]=n/32768;}const buffer=ctx.createBuffer(1,samples.length,24000);buffer.copyToChannel(samples,0);const source=ctx.createBufferSource();(source as any).syntheticInput=true;source.buffer=buffer;source.connect(destination);audit.inputEnd=performance.now()+buffer.duration*1000;source.start();};
   const track=destination.stream.getAudioTracks()[0],stop=track.stop.bind(track);track.stop=()=>{audit.stops++;stop();void ctx.close();};return destination.stream;
  }});
 },{base64:pcm.toString('base64')});
 page=await context.newPage();page.on('pageerror',error=>console.log('Browser script error:',error.message));
 for(let n=0;n<count;n++){
  const conversation=await service.store.createConversation(owner.id);await page.goto(origin+'/app/'+conversation.id);await page.getByLabel('Talk to Marvin').click();const before=await page.evaluate(()=>(window as any).voiceAudit.captures);if(before!==0)throw new Error('Capture began before user gesture');
  await page.getByRole('button',{name:'Start voice',exact:true}).click();await page.getByRole('heading',{name:'I’m listening'}).waitFor({timeout:30000});await page.evaluate(()=>(window as any).speakSynthetic());
  let complete=false;for(let i=0;i<900;i++){const turns=await service.store.turns(owner.id,conversation.id);if(turns.some(t=>t.status==='completed')){complete=true;break;}if(turns.some(t=>t.status==='failed'||t.status==='cancelled'))break;await page.waitForTimeout(100);}
  await page.getByRole('button',{name:'Continue in text',exact:true}).click();const audit=await page.evaluate(()=>(window as any).voiceAudit);const turns=await service.store.turns(owner.id,conversation.id);const passed=complete&&audit.captures===1&&audit.stops===1&&audit.playbackStarts>0&&turns.length===1&&!!turns[0].userText&&!!turns[0].assistantText;
  results.push({sample:n+1,passed,...audit,inputEndToScheduledPlaybackMs:audit.firstPlayback-audit.inputEnd});await writeFile(directory+`/answer-${n+1}.json`,JSON.stringify(turns.map(t=>({user:t.userText,assistant:t.assistantText,status:t.status}))),{mode:0o600});await report();console.log(JSON.stringify(results.at(-1)));if(!passed){process.exitCode=1;break;}
 }
}catch(e){fatal=e instanceof Error?e.message.split('\n')[0]:'Browser voice failed';if(page){await writeFile(directory+'/browser.txt',await page.locator('body').innerText());console.log(await page.evaluate(()=>({audit:(window as any).voiceAudit,shim:String(navigator.mediaDevices.getUserMedia).slice(0,150)})));await page.screenshot({path:directory+'/browser.png'});}process.exitCode=1;}finally{await browser?.close();await service.app.close();await report();}
async function report(){await writeFile(directory+'/results.json',JSON.stringify({at:new Date().toISOString(),model:cfg.OPENAI_REALTIME_MODEL,target:count,results,fatal,scope:'Generated PCM feeds a synthetic getUserMedia stream through the real browser capture AudioWorklet, backend, live provider and browser playback. Input-end to scheduled Web Audio output includes VAD. No room microphone or physical speaker/acoustic measurement.'},null,2));}
