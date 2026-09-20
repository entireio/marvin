import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {readFileSync,mkdirSync,writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createPublicKey,verify} from 'node:crypto';
import {z} from 'zod';
import {Security2} from '../packages/provisioning/src/security2.js';
const directory=process.argv[2];if(!directory)throw new Error('Provide the private factory directory. Credentials are never printed.');
const expectedSsid=process.env.MARVIN_EXPECT_SSID;
const wifiFlag=process.argv.indexOf('--wifi-file');
const wifi=wifiFlag>=0?z.object({ssid:z.string().min(1).refine(s=>Buffer.byteLength(s)<=32),password:z.string().max(63)}).strict().parse(JSON.parse(readFileSync(process.argv[wifiFlag+1],'utf8'))):null;
const credentials=JSON.parse(readFileSync(resolve(directory,'setup-secret.json'),'utf8'));
const child=spawn(resolve('work/idf-tools/python_env/idf5.4_py3.13_env/bin/python'),['firmware/tools/ble-bridge.py'],{stdio:['pipe','pipe','inherit']});
const lines=createInterface({input:child.stdout})[Symbol.asyncIterator]();
async function receive(){let timer:ReturnType<typeof setTimeout>;try{return await Promise.race([lines.next().then(({value,done})=>{if(done)throw new Error('BLE transport ended');const data=JSON.parse(value);if(data.error)throw new Error('BLE transport: '+data.error);return data;}),new Promise<never>((_,reject)=>{timer=setTimeout(()=>reject(new Error('BLE transport timed out')),45000);})]);}finally{clearTimeout(timer!);}}
async function transport(channel:string,bytes?:Uint8Array){child.stdin.write(JSON.stringify({op:bytes?'exchange':'read',channel,...(bytes?{hex:Buffer.from(bytes).toString('hex')}:{})})+'\n');return new Uint8Array(Buffer.from((await receive()).hex,'hex'));}
const secure=new Security2(),decoder=new TextDecoder();
try{
 await receive();console.log('BLE connected; reading protocol version');const version=JSON.parse(decoder.decode(await transport('ff51',new TextEncoder().encode('---'))));if(version.security!==2||version.patch!==1)throw new Error('Require Security 2 patch 1');
 console.log('Opening Security 2 session');
 if(process.argv.includes('--bad-proof')){
  let rejected=false,exchanges=0;try{await secure.open(credentials.username,credentials.password+'_incorrect',data=>{exchanges++;return transport('ff52',data);});}catch(error){if(error instanceof Error&&exchanges===2&&/rejected|prove|BleakGATTProtocolError/.test(error.message))rejected=true;else throw error;}
  if(!rejected)throw new Error('Invalid setup proof was accepted');
  console.log(JSON.stringify({badProofRejected:true}));
 }else{
 await secure.open(credentials.username,credentials.password,data=>transport('ff52',data));
 const command=async(value:object)=>JSON.parse(decoder.decode(await secure.exchange(new TextEncoder().encode(JSON.stringify(value)),data=>transport('ff53',data))));
 console.log('Secure session established; verifying identity');const proof=await command({op:'challenge',operation:'claim',issuedAt:Date.now()});if(proof.error)throw new Error(proof.error);
 const c=proof.challenge,message=`marvin-setup-v1:${c.deviceId}:${c.nonce}:${c.operation}:${c.issuedAt}`;
 if(c.deviceId!==credentials.deviceId||!verify('sha256',Buffer.from(message),{key:createPublicKey(readFileSync(resolve(directory,'device-public.pem'))),dsaEncoding:'ieee-p1363'},Buffer.from(proof.proof,'base64url')))throw new Error('Device identity proof failed');
 const initial=await command({op:'status'});
 if(process.argv.includes('--verify-saved')){
  const result={at:new Date().toISOString(),passed:initial.hasSavedNetwork===true&&initial.networkConnected===true,hasSavedNetwork:initial.hasSavedNetwork,networkConnected:initial.networkConnected,note:'Saved network reconnected after board reset; no credentials reapplied.'};
  writeFileSync('work/board/wifi-reboot.json',JSON.stringify(result,null,2));if(!result.passed)throw new Error('Saved Wi-Fi did not reconnect after reset');
 }
 console.log('Identity verified; requesting robot scan');await command({op:'scan'});let status:any;
 for(let i=0;i<30;i++){status=await command({op:'status'});if(status.phase==='scan_complete')break;if(status.phase==='failed')throw new Error('Radio scan failed');await new Promise(r=>setTimeout(r,500));}
 if(status.phase!=='scan_complete'||!Number.isInteger(status.count)||status.count<0||status.count>32)throw new Error('Incomplete radio scan');
 let chosen=-1,expectedVisible=false,expectedSupported=false;const networks=[];for(let i=0;i<status.count;i++){const n=await command({op:'network',index:i});if(n.source!=='robot'||n.channel<1||n.channel>14)throw new Error('Invalid robot radio result');if(expectedSsid&&n.ssid===expectedSsid){expectedVisible=true;expectedSupported ||= n.supported;}if(wifi&&chosen<0&&n.ssid===wifi.ssid&&n.supported)chosen=i;networks.push({channel:n.channel,rssi:n.rssi,supported:n.supported});}
 if(wifi){
  if(chosen<0)throw new Error('The configured test network was not visible and supported in Marvin’s scan.');
  const clock=await command({op:'clock',utcMs:Date.now()});if(!clock.clockSet)throw new Error('Authenticated setup clock was rejected');
  const started=Date.now(),expectedFailure=process.argv.includes('--wrong-wifi');
  const response=await command({op:'apply',index:chosen,scanGeneration:status.scanGeneration,password:expectedFailure?'deliberately-invalid-wifi-secret':wifi.password});wifi.password='';
  if(response.error)throw new Error('Wi-Fi apply rejected: '+response.error);
  let finished:any;for(let i=0;i<95;i++){finished=await command({op:'status'});if(['network_verified','failed'].includes(finished.phase))break;await new Promise(r=>setTimeout(r,1000));}
  const passed=expectedFailure?finished.phase==='failed'&&finished.error==='WIFI_CONNECTION_FAILED'&&(!initial.hasSavedNetwork||finished.networkConnected===true):finished.phase==='network_verified';
  const result={at:new Date().toISOString(),passed,expectedFailure,phase:finished.phase,error:finished.error,hasSavedNetwork:finished.hasSavedNetwork,networkConnected:finished.networkConnected,elapsedMs:Date.now()-started,accountLinked:false,note:'Bench Wi-Fi and TLS probe only. Password stayed on the encrypted BLE path; no owner claim.'};
  writeFileSync('work/board/wifi-'+(expectedFailure?'rollback':'smoke')+'.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result));if(!passed)throw new Error('Wi-Fi bench acceptance failed');
 }
 mkdirSync('work/board',{recursive:true});const result={at:new Date().toISOString(),passed:true,security:2,patch:1,deviceProofVerified:true,networkCount:networks.length,networks,...(expectedSsid?{expectedVisible,expectedSupported}:{}),note:wifi?'SSID and setup secrets excluded; Wi-Fi result recorded separately.':'SSID and setup secrets excluded. Scan only; no Wi-Fi configuration or ownership claim.'};writeFileSync('work/board/ble-smoke.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
}
}finally{secure.close();child.stdin.end(JSON.stringify({op:'close'})+'\n');child.kill();}
