import {it,expect} from 'vitest';
import {PhysicalRobot,canResumePhysicalSetup,type EnrollmentApi,type SecureSetupLink} from '../../packages/provisioning/src/physical.js';
import {DomainError} from '../../packages/contracts/src/index.js';
function rig(change=false){
 const commands:Record<string,unknown>[]=[],http:unknown[]=[],deviceId='marvin_'+'a'.repeat(32);let applied=false,appliedNetwork='Robot-visible AP',verified=false,closed=false,foreign=false,pending=false;
 const link:SecureSetupLink={reconciliation:true,async command(r){commands.push(r);if(r.op==='clock')return {clockSet:true};if(r.op==='scan')return {phase:'scanning'};if(r.op==='status')return {phase:pending?'awaiting_backend':applied?'network_verified':'scan_complete',linked:applied,networkConnected:true,hasSavedNetwork:applied,scanGeneration:4,count:1,error:'',...(pending?{pendingNetwork:'Robot-visible AP'}:{})};if(r.op==='network')return {source:'robot',ssid:'Robot-visible AP',rssi:-62,channel:6,scanGeneration:4,supported:true,security:'wpa2-personal'};if(r.op==='challenge')return {challenge:{deviceId:foreign?'marvin_'+'b'.repeat(32):deviceId,nonce:'1'.repeat(64),operation:r.operation,issuedAt:r.issuedAt},proof:'synthetic-proof'};if(r.op==='ticket_begin')return {accepted:true};if(r.op==='ticket_chunk')return {received:Number(r.offset)+String(r.hex).length/2};if(r.op==='ticket_finish'){verified=true;return {verified:true};}if(r.op==='clear_owner'){applied=false;return {cleared:true};}if(r.op==='apply'||r.op==='apply_hidden'){expect(verified).toBe(true);applied=true;if(r.op==='apply_hidden')appliedNetwork=String(r.ssid);return {phase:'connecting'};}if(r.op==='resume'){pending=false;applied=true;return {phase:'connecting'};}throw new Error('Unknown command');},close(){closed=true;}};
 const server:EnrollmentApi={async ticket(r){http.push(r);return {ticket:'signed-ticket'.repeat(50),enrollmentId:'enrollment',expiresAt:Date.now()+120000};},async cancel(id){http.push({cancel:id});},async binding(){return applied?{device_id:deviceId,network:appliedNetwork,simulated:0}:null;},async acknowledge(device){http.push({acknowledge:device});}};
 return {robot:new PhysicalRobot(deviceId,link,server,change,1),commands,http,server,link,foreign:()=>{foreign=true;},pending:()=>{pending=true;},closed:()=>closed};
}
it('robot scan indices and ticket chunks precede encrypted password delivery; HTTP gets no Wi-Fi credentials',async()=>{
 const r=rig(),scan=await r.robot.scan();expect(scan.source).toBe('robot');expect(scan.networks[0].ssid).toBe('Robot-visible AP');const stages:string[]=[];await r.robot.connect(scan.networks[0],'synthetic-password',stage=>stages.push(stage),new AbortController().signal);
 expect(stages).toEqual(['connecting_wifi','ready']);expect(r.commands.filter(c=>c.op==='ticket_chunk').every(c=>String(c.hex).length<=240)).toBe(true);expect(r.commands.find(c=>c.op==='apply')).toMatchObject({index:0,scanGeneration:4,password:'synthetic-password'});expect(JSON.stringify(r.http)).not.toMatch(/synthetic-password|Robot-visible AP/);expect((r.http[0] as any).challenge.operation).toBe('claim');
});
it('a network change requires the same identity and a network ticket',async()=>{const r=rig(true),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal);expect((r.http[0] as any).challenge.operation).toBe('network');});
it('forged networks and a different device fail before passwords or HTTP authorization',async()=>{
 const r=rig(),scan=await r.robot.scan();await expect(r.robot.connect({...scan.networks[0],ssid:'Laptop-only AP'},'synthetic-password',()=>{},new AbortController().signal)).rejects.toMatchObject({code:'NETWORK_NOT_SCANNED'});r.foreign();await expect(r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal)).rejects.toMatchObject({code:'DEVICE_IDENTITY_MISMATCH'});expect(r.http).toHaveLength(0);expect(r.commands.some(c=>'password'in c)).toBe(false);
});
it('cancellation while the backend issues a ticket closes BLE and cancels only the unused reservation',async()=>{
 const r=rig(),scan=await r.robot.scan();let resolve:((v:any)=>void)|undefined;r.server.ticket=()=>new Promise(r=>{resolve=r;});const abort=new AbortController(),work=r.robot.connect(scan.networks[0],'synthetic-password',()=>{},abort.signal);for(let i=0;!resolve&&i<20;i++)await new Promise(done=>setTimeout(done,0));expect(resolve).toBeDefined();abort.abort();resolve!({ticket:'ticket',enrollmentId:'unused',expiresAt:Date.now()+120000});await expect(work).rejects.toThrow();expect(r.closed()).toBe(true);expect(r.commands.some(c=>'password'in c)).toBe(false);expect(r.http).toContainEqual({cancel:'unused'});
});
it('resume uses the robot pending network and confirms the server binding without resending credentials',async()=>{const r=rig();r.pending();const stages:string[]=[];await r.robot.resume(undefined,s=>stages.push(s),new AbortController().signal);expect(stages).toContain('ready');expect(r.commands.some(c=>'password'in c)).toBe(false);expect(r.http).toHaveLength(0);});
it('resume after reboot confirms the saved network and binding without a new ticket or apply',async()=>{
 const r=rig(),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal);const command=r.link.command.bind(r.link);r.commands.length=0;r.http.length=0;
 r.link.command=async request=>{const value=await command(request) as any;return request.op==='status'?{...value,phase:'idle',savedNetwork:'Robot-visible AP'}:value;};
 await r.robot.resume(undefined,()=>{},AbortSignal.timeout(1000));expect(r.commands.map(c=>c.op)).toEqual(['status','status']);expect(r.http).toHaveLength(0);
 r.server.binding=async()=>({device_id:'another-device',network:'Robot-visible AP',simulated:0});await expect(r.robot.resume(undefined,()=>{},AbortSignal.timeout(1000))).rejects.toMatchObject({code:'STALE_LOCAL_LINK'});
});
it('clears an authorized stale local link before a fresh scan',async()=>{const r=rig(),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal);r.server.binding=async()=>null;r.commands.length=0;r.http.length=0;const stages:string[]=[];await expect(r.robot.reconcile(stage=>stages.push(stage),AbortSignal.timeout(1000))).resolves.toBe(true);expect(stages).toEqual(['reconciling']);expect((r.http[0] as any).challenge.operation).toBe('reconcile');expect(r.commands.at(-1)).toMatchObject({op:'clear_owner'});expect(r.http).toContainEqual({acknowledge:'marvin_'+'a'.repeat(32)});});
it('continues reconciliation only when an ownership-state reply is followed by an unlinked pet status',async()=>{
 const r=rig(),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal);r.server.binding=async()=>null;const command=r.link.command.bind(r.link);let initial=true;
 r.link.command=async request=>{if(request.op==='clear_owner')return {error:'OWNERSHIP_STATE'};if(request.op==='status'&&!initial)return {phase:'scan_complete',linked:false,networkConnected:true,hasSavedNetwork:true,scanGeneration:4,count:1,error:''};if(request.op==='status')initial=false;return command(request);};
 await expect(r.robot.reconcile(()=>{},AbortSignal.timeout(1000))).resolves.toBe(true);expect(r.http).toContainEqual({acknowledge:'marvin_'+'a'.repeat(32)});
});
it('does not claim that old firmware can repair a stale local link',async()=>{const r=rig(),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal);r.server.binding=async()=>null;r.link.reconciliation=false;await expect(r.robot.reconcile(()=>{},AbortSignal.timeout(1000))).rejects.toMatchObject({code:'FIRMWARE_RECONCILIATION_REQUIRED'});expect(r.commands.filter(c=>c.op==='challenge')).toHaveLength(1);});

it('setup cards reject malformed or extra data without echoing private input',async()=>{const {decodeSetupCode}=await import('../../packages/provisioning/src/setup-code.js');const card={deviceId:'marvin_'+'a'.repeat(32),username:'marvin-test',password:'private-synthetic-code'};expect(decodeSetupCode('marvin1.'+Buffer.from(JSON.stringify(card)).toString('base64url'))).toEqual(card);try{decodeSetupCode('private-synthetic-code');throw new Error('unexpected');}catch(e){expect((e as Error).message).not.toContain('private-synthetic-code');}expect(()=>decodeSetupCode('marvin1.'+Buffer.from(JSON.stringify({...card,privateKey:'never allowed'})).toString('base64url'))).toThrow();});

it('confirmed rollback releases the unused server reservation for an immediate password retry',async()=>{const r=rig(),scan=await r.robot.scan(),command=r.link.command.bind(r.link);r.link.command=async request=>{const reply=await command(request) as any;return request.op==='status'&&reply.phase==='network_verified'?{...reply,phase:'failed',linked:false,error:'WIFI_CONNECTION_FAILED'}:reply;};await expect(r.robot.connect(scan.networks[0],'synthetic-password',()=>{},new AbortController().signal)).rejects.toMatchObject({code:'WIFI_CONNECTION_FAILED'});expect(r.http).toContainEqual({cancel:'enrollment'});});

it('classifies only post-commit or service uncertainty as resumable',()=>{
 for(const code of ['SETUP_PENDING','BLUETOOTH_DISCONNECTED','BLUETOOTH_TIMEOUT','ENROLLMENT_RETRY','BACKEND_UNREACHABLE','TLS_OR_TRANSPORT_FAILED','BACKEND_HTTP_FAILED','CLOCK_SYNC_FAILED'])expect(canResumePhysicalSetup(new DomainError(code,'fixture'))).toBe(true);
 for(const code of ['WIFI_CONNECTION_FAILED','WIFI_AUTH_FAILED','ENROLLMENT_REVOKED','CHALLENGE_EXPIRED','DEVICE_IDENTITY_MISMATCH'])expect(canResumePhysicalSetup(new DomainError(code,'fixture'))).toBe(false);
 expect(canResumePhysicalSetup(new Error('fixture'))).toBe(false);
});

it('maps a lost BLE transport to an actionable resumable error without exposing its cause',async()=>{
 const r=rig();r.link.command=async()=>{throw new Error('private native transport detail');};
 await expect(r.robot.scan()).rejects.toMatchObject({code:'BLUETOOTH_DISCONNECTED',message:'Reconnect to your Desktop Pet to continue.'});
});

it('normalizes firmware recovery failures during encrypted status polling',async()=>{
 for(const [code,message] of [
  ['ENROLLMENT_RETRY','could not confirm the account link'],
  ['BACKEND_UNREACHABLE','could not contact the Marvin service'],
  ['TLS_OR_TRANSPORT_FAILED','secure connection'],
  ['BACKEND_HTTP_FAILED','connectivity check'],
  ['CLOCK_SYNC_FAILED','set its clock securely'],
  ['PREVIOUS_NETWORK_UNAVAILABLE','previous network']
 ] as const){
  const r=rig(),command=r.link.command.bind(r.link);r.pending();r.link.command=async request=>request.op==='status'?{error:code}:command(request);
  await expect(r.robot.resume(undefined,()=>{},AbortSignal.timeout(1000))).rejects.toMatchObject({code,message:expect.stringContaining(message)});
 }
});

it('merges only equivalent AP records, retains the strongest robot record and sorts by signal',async()=>{
 const r=rig(),command=r.link.command.bind(r.link),aps=[
  {source:'robot',ssid:'Mesh',rssi:-75,channel:1,scanGeneration:4,supported:true,security:'wpa2-personal'},
  {source:'robot',ssid:'Guest',rssi:-50,channel:6,scanGeneration:4,supported:true,security:'open'},
  {source:'robot',ssid:'Mesh',rssi:-42,channel:11,scanGeneration:4,supported:true,security:'wpa2-personal'},
  {source:'robot',ssid:'Mesh',rssi:-38,channel:1,scanGeneration:4,supported:false,security:'wpa3-personal'}
 ];
 r.link.command=async request=>request.op==='status'?{phase:'scan_complete',linked:false,networkConnected:false,hasSavedNetwork:false,scanGeneration:4,count:aps.length,error:''}:request.op==='network'?aps[Number(request.index)]:command(request);
 const scan=await r.robot.scan();expect(scan.networks.map(n=>[n.ssid,n.security,n.rssi,n.channel,n.compatible])).toEqual([['Mesh','wpa3-personal',-38,1,false],['Mesh','wpa2-personal',-42,11,true],['Guest','open',-50,6,true]]);
});

it('supports an explicit bounded hidden WPA2 network without claiming it came from the scan',async()=>{
 const r=rig(),network=r.robot.hiddenNetwork('Hidden fixture');await r.robot.connect(network,'synthetic-password',()=>{},AbortSignal.timeout(1000));
 expect(r.commands.find(c=>c.op==='apply_hidden')).toMatchObject({ssid:'Hidden fixture',security:'wpa2-personal',password:'synthetic-password'});expect(r.commands.some(c=>c.op==='apply')).toBe(false);expect(JSON.stringify(r.http)).not.toMatch(/Hidden fixture|synthetic-password/);
 expect(()=>r.robot.hiddenNetwork('é'.repeat(17))).toThrowError(/1 and 32 bytes/);
});

it('allows connection from an older unchanged robot scan',async()=>{
 const r=rig(),scan=await r.robot.scan();await r.robot.connect(scan.networks[0],'synthetic-password',()=>{},AbortSignal.timeout(1000));expect(r.http).toHaveLength(1);expect(r.commands.some(c=>c.op==='apply')).toBe(true);
});
