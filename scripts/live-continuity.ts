/** Billable live text–voice–text recall checks using generated speech and disposable state. */
import {mkdir,writeFile,readFile} from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';
import WebSocket from 'ws';
import {config} from '../apps/server/src/config.js';
import {createApp} from '../apps/server/src/app.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
try{process.loadEnvFile('.env');}catch{}
if(!process.env.OPENAI_API_KEY)throw new Error('Configure the API key privately.');
const directory='work/m5/continuity-'+Date.now();await mkdir(directory,{recursive:true,mode:0o700});
const scenarios=[
 {seed:'The project code word is lantern.',question:'What is the project code word?',expected:'lantern'},
 {seed:'The robot enclosure color is violet.',question:'What color is the robot enclosure?',expected:'violet'},
 {seed:'The demonstration is scheduled for Thursday.',question:'Which day is the demonstration?',expected:'thursday'},
 {seed:'Our test operator is called Oliver.',question:'What is the name of our test operator?',expected:'oliver'},
 {seed:'For the picnic, remember that I avoid peanuts.',question:'Which food do I avoid for the picnic?',expected:'peanut'},
];
for(const [i,s] of scenarios.entries()){
 execFileSync('/usr/bin/say',['-o',`${directory}/input-${i}.aiff`,s.question],{stdio:'ignore'});
 execFileSync('/opt/homebrew/bin/ffmpeg',['-v','error','-i',`${directory}/input-${i}.aiff`,'-ar','24000','-ac','1','-f','s16le',`${directory}/input-${i}.pcm`],{stdio:'ignore'});
}
const cfg=config({NODE_ENV:'test',AUTH_MODE:'development',APP_ORIGIN:'http://127.0.0.1:5173',MODEL_PROVIDER:'openai',VOICE_PROVIDER:'openai',OPENAI_API_KEY:process.env.OPENAI_API_KEY,OPENAI_MODEL:process.env.OPENAI_MODEL,OPENAI_REALTIME_MODEL:process.env.OPENAI_REALTIME_MODEL});
const service=await createApp(cfg,{database:sqliteDatabase()});const address=(await service.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:');const results:unknown[]=[];
try{
 for(let n=0;n<20;n++){
  const scenario=scenarios[n%5],owner=await service.store.ensureOwner('test','continuity-'+n,'Synthetic test'),conversation=await service.store.createConversation(owner.id),session=await service.store.createSession(owner.id);
  const text=async(input:string)=>{await service.runtime.start(await service.runtime.context(owner.id,conversation.id,randomUUID(),'text'),input);await service.runtime.drain();};
  await text(scenario.seed+' Remember this for our conversation. Acknowledge briefly.');
  const socket=new WebSocket(address+'/api/voice',{headers:{cookie:'marvin_session='+session.token,origin:cfg.APP_ORIGIN}});let ready=false,done=false,error:string|null=null,audioBytes=0;
  socket.on('message',raw=>{const e=JSON.parse(raw.toString());if(e.type==='ready')ready=true;if(e.type==='audio')audioBytes+=Buffer.from(e.pcm,'base64').length;if(e.type==='error'){error='VOICE_ERROR';done=true;}if(e.type==='event'&&['completed','error'].includes(e.event.type)){done=true;if(e.event.type==='error')error='TURN_ERROR';}});
  socket.on('error',()=>{error='TRANSPORT_ERROR';done=true;});socket.on('close',()=>{if(!done){error='EARLY_CLOSE';done=true;}});
  try{
   await new Promise<void>((resolve,reject)=>{socket.once('open',resolve);socket.once('error',()=>reject(new Error('Transport failed')));});socket.send(JSON.stringify({v:1,type:'start',conversationId:conversation.id,csrf:session.csrf}));
   const deadline=Date.now()+90000;while(!ready&&!done&&Date.now()<deadline)await delay(10);
   if(ready){const pcm=Buffer.concat([Buffer.alloc(4800),await readFile(`${directory}/input-${n%5}.pcm`),Buffer.alloc(48000)]);for(let offset=0;offset<pcm.length&&!done;offset+=960){socket.send(pcm.subarray(offset,offset+960));await delay(20);}while(!done&&Date.now()<deadline)await delay(10);}
   if(!done)error='TIMEOUT';
  }finally{socket.terminate();await service.runtime.drain();}
  await text('Repeat the answer to the question I just asked by voice. Keep it brief.');
  const turns=await service.store.turns(owner.id,conversation.id),voice=turns.find(t=>t.surface==='web_voice'),last=turns.at(-1);
  const passed=!error&&audioBytes>0&&turns.length===3&&turns.every(t=>t.status==='completed')&&!!voice?.assistantText.toLowerCase().includes(scenario.expected)&&!!last?.assistantText.toLowerCase().includes(scenario.expected);
  await writeFile(`${directory}/journey-${n+1}.json`,JSON.stringify({scenario,turns:turns.map(t=>({surface:t.surface,user:t.userText,assistant:t.assistantText,status:t.status})),error,audioBytes,passed},null,2),{mode:0o600});
  results.push({case:n+1,scenario:n%5+1,passed,error,audioBytes});await report();console.log(JSON.stringify(results.at(-1)));if(error){process.exitCode=1;break;}
 }
}finally{await service.app.close();await report();}
async function report(){await writeFile(directory+'/results.json',JSON.stringify({at:new Date().toISOString(),textModel:cfg.OPENAI_MODEL,voiceModel:cfg.OPENAI_REALTIME_MODEL,results,scope:'20 live text–voice–text journeys, five synthetic factual recall scenarios repeated across fresh owners/conversations/provider sessions. No room audio. Substring recall checks require answer review; this does not establish compaction, repository changes, interruption or acoustic latency acceptance.'},null,2));}
