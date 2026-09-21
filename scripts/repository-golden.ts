/** Read-only reference-repository checks. Optional live answers are private and require manual semantic review. */
import {mkdir,writeFile,readFile} from 'node:fs/promises';
import {config} from '../apps/server/src/config.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
import {EntireCli} from '../packages/runtime/src/entire-cli.js';
import {OpenAITextProvider} from '../packages/runtime/src/provider.js';
import {randomUUID,createHash} from 'node:crypto';
try{process.loadEnvFile('.env');}catch{}
const live=process.argv.includes('--live'),cfg=config();
const selected=process.argv.find(a=>a.startsWith('--cases='))?.slice(8).split(',').map(Number);
if(!cfg.ENTIRE_CLI_PATH||!cfg.ENTIRE_BINDINGS_FILE)throw new Error('Configure the authorized local Entire integration.');
if(live&&(!cfg.OPENAI_API_KEY||!cfg.OPENAI_MODEL))throw new Error('Live evaluation requires the configured model and key; each question is billable.');
const db=sqliteDatabase(cfg.SQLITE_PATH);const owners=await db.query<{id:string}>('SELECT id FROM owners WHERE issuer=? AND subject=?',[cfg.AUTH_MODE==='local'?'local':'development','owner']);await db.close();if(owners.length!==1)throw new Error('Sign in to the portal first.');
const owner=owners[0].id,adapter=new EntireCli(cfg.ENTIRE_CLI_PATH,cfg.ENTIRE_BINDINGS_FILE),repo=(await adapter.connect(owner)).find(r=>r.name==='spedemon/marvin');if(!repo)throw new Error('The authorized reference repository spedemon/marvin is not available.');
const cases:[string,string,number,number,RegExp][]=[
 ['Which ESP32 family does this repository currently target?','firmware/platformio.ini',1,30,/ESP32-C3/],
 ['Which PlatformIO board definition does it use?','firmware/platformio.ini',1,30,/lolin_c3_mini/],
 ['Which framework does its firmware use?','firmware/platformio.ini',1,30,/arduino/],
 ['What is the serial monitor baud rate?','firmware/platformio.ini',1,30,/115200/],
 ['Which GPIO controls head tilt?','firmware/src/config.h',1,55,/PIN_SERVO_TILT\s+1\b/],
 ['Which GPIO controls head pan?','firmware/src/config.h',1,55,/PIN_SERVO_ROTATION\s+4\b/],
 ['Which driver is named for the DC motors?','firmware/src/config.h',1,55,/DRV8833/],
 ['Which pins drive motor A?','firmware/src/config.h',1,55,/PIN_MOTOR_A_IN1\s+5[\s\S]*PIN_MOTOR_A_IN2\s+6/],
 ['Which pins drive motor B?','firmware/src/config.h',1,55,/PIN_MOTOR_B_IN3\s+20[\s\S]*PIN_MOTOR_B_IN4\s+10/],
 ['What servo pulse-width limits are configured?','firmware/src/config.h',1,55,/SERVO_MIN_PULSE_WIDTH\s+500[\s\S]*SERVO_MAX_PULSE_WIDTH\s+2400/],
 ['How often are servo positions updated?','firmware/src/config.h',1,55,/SERVO_UPDATE_INTERVAL_MS\s+20/],
 ['How many degrees does a servo advance in one update?','firmware/src/config.h',1,55,/SERVO_STEP_SIZE\s+2.0f/],
 ['What tilt-angle range is configured?','firmware/src/config.h',1,55,/SERVO_TILT_MIN\s+30[\s\S]*SERVO_TILT_MAX\s+85/],
 ['What is the neutral tilt angle?','firmware/src/config.h',1,55,/SERVO_TILT_CENTER\s+57/],
 ['What are the slow, medium and fast motor presets?','firmware/src/config.h',1,55,/MOTOR_SPEED_SLOW\s+80[\s\S]*MOTOR_SPEED_MEDIUM\s+140[\s\S]*MOTOR_SPEED_FAST\s+200/],
 ['Is demo mode enabled at boot?','firmware/src/config.h',1,55,/DEMO_ENABLED_AT_BOOT false/],
 ['What BLE name does the firmware advertise?','firmware/src/config.h',1,55,/BLE_DEVICE_NAME "Marvin"/],
 ['How are the motor pins initialized to prevent startup spin?','firmware/src/motor.cpp',1,40,/digitalWrite\(_pinIN1, LOW\)[\s\S]*digitalWrite\(_pinIN2, LOW\)/],
 ['How does the motor driver handle speed outside its allowed range?','firmware/src/motor.cpp',1,40,/constrain\(speed, -MOTOR_MAX_SPEED, MOTOR_MAX_SPEED\)/],
 ['Which local port serves the standalone controller?','controller/README.md',1,30,/localhost:3000/],
];
const runDirectory='work/m4/golden/'+(live?'live-':'retrieval-')+Date.now();await mkdir(runDirectory,{recursive:true,mode:0o700});const sourceHash=createHash('sha256').update(await readFile('packages/runtime/src/provider.ts')).update(await readFile('packages/runtime/src/entire-cli.ts')).update(await readFile('packages/runtime/src/policy.ts')).digest('hex');const results:unknown[]=[],provider=live?new OpenAITextProvider(cfg.OPENAI_API_KEY!,cfg.OPENAI_MODEL!,undefined,cfg.OPENAI_REPOSITORY_MODEL):null;
for(const [index,[question,path,start,end,expected]] of cases.entries()){
 if(selected&&!selected.includes(index+1))continue;
 const card=await adapter.read(owner,repo.id,'repository_source',{path,start,end}),evidence=card.evidence?.find(e=>e.path===path),retrieved=!!evidence&&evidence.origin==='git'&&expected.test(evidence.excerpt??'');
 let response='',modelError=false;const citations:{path?:string;revision?:string}[]=[];const toolTrace:unknown[]=[];
 if(provider)try{for await(const event of provider.run({interaction:{ownerId:owner,conversationId:'golden',interactionId:randomUUID(),routeId:'golden',surface:'web_text',repositoryId:repo.id,repositoryCapabilities:repo.capabilities,entireState:'connected',body:null},summary:'',messages:[],input:question+' Answer about the selected repository, not the separate Marvin_software project. Verify committed source and cite its path, lines and revision.'},async(tool,args)=>{let result;try{result=await adapter.read(owner,repo.id,tool,args as Record<string,unknown>);toolTrace.push({tool,args,ok:true});}catch(e){toolTrace.push({tool,args,ok:false,error:e instanceof Error?e.name:'unknown',code:(e as {code?:string}).code});throw e;}for(const e of result.evidence??[])citations.push({path:e.path,revision:e.revision});return result;},AbortSignal.timeout(90000)))if(event.type==='delta')response+=event.text;}catch{modelError=true;}
 if(live)await writeFile(`${runDirectory}/answer-${index+1}.json`,JSON.stringify({question,response,evidence:citations,toolTrace,manualRubric:'Answer the question correctly, use only retrieved evidence, distinguish original C3 firmware from the new S3 software, and cite path/lines/revision. Review every factual assertion.'},null,2),{mode:0o600});
 results.push({case:index+1,path,revision:card.revision,retrievalPassed:retrieved,liveAttempted:live,modelError,answerReceived:!!response,manualSemanticReviewRequired:live});
 const report=JSON.stringify({at:new Date().toISOString(),runDirectory,sourceHash,model:live?(cfg.OPENAI_REPOSITORY_MODEL??cfg.OPENAI_MODEL):null,repository:'spedemon/marvin',live,results,acceptance:'Retrieval conformance only until all live answers receive independent semantic/citation review; missing-data, graph and service capability gates are separate.'},null,2);await writeFile(runDirectory+'/results.json',report);await writeFile('work/m4/golden/results.json',report);
 console.log(JSON.stringify({case:index+1,retrievalPassed:retrieved,liveAttempted:live,modelError}));if(!retrieved||modelError){process.exitCode=1;break;}
}
