import { readFile,mkdir,writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { parseArgs } from 'node:util';
import { performance } from 'node:perf_hooks';
import WebSocket from 'ws';
import { EntireCli } from '../packages/runtime/src/entire-cli.js';
import { OpenAIVoiceProvider } from '../packages/runtime/src/openai-voice.js';
import { config } from '../apps/server/src/config.js';
import { createApp } from '../apps/server/src/app.js';
import { sqliteDatabase } from '../packages/persistence/src/database.js';
try{process.loadEnvFile('.env');}catch{}
const {values}=parseArgs({options:{input:{type:'string'},repository:{type:'boolean',default:false}}});
if(!values.input)throw new Error('Supply --input /path/to/mono-24000Hz-s16le.pcm (synthetic or consented test audio).');
const pcm=await readFile(values.input);if(!pcm.length||pcm.length%2||pcm.length>24000*2*60)throw new Error('Input must be PCM s16le, mono 24 kHz, at most 60 seconds.');
const cfg=config({...process.env,NODE_ENV:'test',AUTH_MODE:'development',HOST:'127.0.0.1',APP_ORIGIN:'http://127.0.0.1:5173',VOICE_PROVIDER:'openai'});
const providerErrors:string[]=[];
const voice=new OpenAIVoiceProvider(cfg.OPENAI_API_KEY!,cfg.OPENAI_REALTIME_MODEL!,cfg.OPENAI_TRANSCRIPTION_MODEL,cfg.OPENAI_VOICE,undefined,undefined,code=>providerErrors.push(code));
const service=await createApp(cfg,{database:sqliteDatabase(),voice});let owner:{id:string};
if(values.repository){
 if(!cfg.ENTIRE_CLI_PATH||!cfg.ENTIRE_BINDINGS_FILE)throw new Error('Configure authorized Entire access.');
 const source=sqliteDatabase(cfg.SQLITE_PATH),rows=await source.query<{id:string}>('SELECT id FROM owners WHERE issuer=? AND subject=?',[process.env.AUTH_MODE==='local'?'local':'development','owner']);await source.close();
 if(rows.length!==1)throw new Error('Sign in to the portal before this authorized repository test.');owner=rows[0];
 await service.store.db.query("INSERT INTO owners(id,issuer,subject,name,entire_state,created_at) VALUES (?,'smoke','repository-voice','Synthetic repository voice','connected',?)",[owner.id,Date.now()]);
}else owner=await service.store.ensureOwner('smoke','voice','Voice smoke');
const conversation=await service.store.createConversation(owner.id),session=await service.store.createSession(owner.id);
if(values.repository){const adapter=new EntireCli(cfg.ENTIRE_CLI_PATH!,cfg.ENTIRE_BINDINGS_FILE!),repo=(await adapter.connect(owner.id)).find(r=>r.name==='spedemon/marvin');if(!repo)throw new Error('Authorized reference repository is unavailable.');await service.store.setRepository(owner.id,conversation.id,repo.id);}

const address=(await service.app.listen({host:'127.0.0.1',port:0})).replace('http:','ws:');const socket=new WebSocket(address+'/api/voice',{headers:{cookie:'marvin_session='+session.token,origin:cfg.APP_ORIGIN}});
let ready=false,done=false,failure:string|undefined,transcript=false,audioBytes=0,endSpeech:number|undefined,firstAudio:number|undefined;
socket.on('message',data=>{const e=JSON.parse(data.toString());if(e.type==='ready')ready=true;if(e.type==='state'&&e.state==='thinking')endSpeech=performance.now();if(e.type==='turn')transcript=!!e.text;if(e.type==='audio'){firstAudio??=performance.now();audioBytes+=Buffer.from(e.pcm,'base64').length;}if(e.type==='error'){failure='VOICE_CONNECTION_OR_PROVIDER_ERROR';done=true;}if(e.type==='event'&&['completed','error'].includes(e.event.type)){done=true;if(e.event.type==='error')failure=e.event.code;}});
socket.on('error',()=>{failure='VOICE_TRANSPORT_ERROR';done=true;});socket.on('close',()=>{if(!done){failure='VOICE_CLOSED';done=true;}});
try{
 await new Promise<void>((resolve,reject)=>{socket.once('open',resolve);socket.once('error',()=>reject(new Error('Voice transport failed')));});socket.send(JSON.stringify({v:1,type:'start',conversationId:conversation.id,csrf:session.csrf}));
 const deadline=performance.now()+90000;while(!ready&&!done&&performance.now()<deadline)await delay(10);
 if(ready){const input=Buffer.concat([Buffer.alloc(4800),pcm,Buffer.alloc(48000)]);for(let at=0;at<input.length&&!done;at+=960){socket.send(input.subarray(at,at+960));await delay(20);}while(!done&&performance.now()<deadline)await delay(10);}
 const turns=await service.store.turns(owner.id,conversation.id);const result={at:new Date().toISOString(),model:cfg.OPENAI_REALTIME_MODEL,success:done&&!failure&&audioBytes>0&&transcript,transcriptSaved:turns.some(t=>t.surface==='web_voice'&&!!t.userText&&!!t.assistantText),audioBytes,firstAudioAfterDetectedEndMs:firstAudio!==undefined&&endSpeech!==undefined?Math.round(firstAudio-endSpeech):null,error:failure??(!done?'TIMEOUT':null),providerErrors,measurement:'Live provider through Marvin backend; synthetic input, no acoustic/browser latency claim'};
 await mkdir('work/m5',{recursive:true});const output=values.repository?'work/m5/repository-voice-'+Date.now():'work/m5/live-voice-smoke';await writeFile(output+'.json',JSON.stringify({...result,repository:values.repository?'spedemon/marvin':null},null,2));if(values.repository)await writeFile(output+'-answers.json',JSON.stringify(turns.map(t=>({user:t.userText,assistant:t.assistantText,cards:t.cards})),null,2),{mode:0o600});console.log(JSON.stringify(result,null,2));if(!result.success)process.exitCode=1;
}finally{socket.close();await service.app.close();}
