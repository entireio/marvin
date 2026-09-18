import {z} from 'zod';
import {DomainError,ScanResult,type Network} from '../../contracts/src/index.js';
import type {ProvisioningTransport,ProvisioningStage} from './index.js';
export interface SecureSetupLink {command(request:Record<string,unknown>):Promise<unknown>;close():void;reconciliation?:boolean;}
export interface EnrollmentApi {
 ticket(request:{challenge:{deviceId:string;nonce:string;operation:'claim'|'network'|'reconcile';issuedAt:number};proof:string}):Promise<{ticket:string;enrollmentId:string;expiresAt:number}>;
 cancel(id:string):Promise<void>;
 binding():Promise<null|{device_id:string;network:string;simulated:number}>;
 acknowledge(deviceId:string):Promise<void>;
}
const Status=z.object({phase:z.string(),linked:z.boolean(),networkConnected:z.boolean(),hasSavedNetwork:z.boolean(),scanGeneration:z.number().int().nonnegative(),count:z.number().int().min(0).max(32),error:z.string().optional(),pendingNetwork:z.string().max(128).optional(),savedNetwork:z.string().max(128).optional()});
const Reply=z.object({error:z.string().optional()}).passthrough();
const Challenge=z.object({deviceId:z.string(),nonce:z.string().regex(/^[a-f0-9]{64}$/),operation:z.enum(['claim','network','reconcile']),issuedAt:z.number().int().positive()});
const resumableCodes=new Set(['SETUP_PENDING','BLUETOOTH_DISCONNECTED','BLUETOOTH_TIMEOUT','ENROLLMENT_RETRY','BACKEND_UNREACHABLE','TLS_OR_TRANSPORT_FAILED','BACKEND_HTTP_FAILED','CLOCK_SYNC_FAILED']);
export function canResumePhysicalSetup(error:unknown){return error instanceof DomainError&&resumableCodes.has(error.code);}
/** Owner-authorized protocol. Wi-Fi passwords only cross the encrypted BLE link. */
export class PhysicalRobot implements ProvisioningTransport {
 readonly simulated=false;private generation=-1;private indices=new Map<string,number>();private selected=new Map<string,Network>();private hidden=new Set<string>();private closed=false;private applied=false;
 constructor(readonly deviceId:string,private link:SecureSetupLink,private server:EnrollmentApi,private change=false,private pollMs=300){}
 private async raw(request:Record<string,unknown>,message:string){if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED',message);try{return await this.link.command(request);}catch(error){if(error instanceof DomainError)throw error;throw new DomainError('BLUETOOTH_DISCONNECTED',message);}}
 private async command(request:Record<string,unknown>){const response=Reply.parse(await this.raw(request,'Reconnect to your Desktop Pet to continue.'));if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to check setup progress.');if(response.error)throw new DomainError(response.error,this.explanation(response.error));return response;}
 private async status(){const response=Reply.parse(await this.raw({op:'status'},'Reconnect to check setup progress.'));if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to check setup progress.');const status=Status.safeParse(response);if(status.success)return status.data;if(response.error)throw new DomainError(response.error,this.explanation(response.error));return Status.parse(response);}
 private explanation(code:string){return ({WIFI_CONNECTION_FAILED:'Desktop Pet could not join that network. Check the password and signal, then try again.',WIFI_AUTH_FAILED:'That Wi-Fi password did not work. Check it and try again.',PREVIOUS_NETWORK_UNAVAILABLE:'The new network failed and Desktop Pet could not return to its previous network. Open setup again to choose another network.',OWNER_TICKET_REQUIRED:'Setup authorization expired. Start the connection again.',SETUP_PENDING:'Your Desktop Pet is still confirming setup. Open setup again to check its progress.',ENROLLMENT_RETRY:'Your Desktop Pet joined Wi-Fi but could not confirm the account link. Open setup again when the service is available.',BACKEND_UNREACHABLE:'Your Desktop Pet joined Wi-Fi but could not contact the Marvin service.',TLS_OR_TRANSPORT_FAILED:'Your Desktop Pet joined Wi-Fi but could not establish a secure connection to the Marvin service.',BACKEND_HTTP_FAILED:'The Marvin service did not accept the connectivity check.',CLOCK_SYNC_FAILED:'Your Desktop Pet joined Wi-Fi but could not set its clock securely.',ENROLLMENT_REVOKED:'This setup is no longer authorized. Start setup again.',CHALLENGE_EXPIRED:'Setup took too long. Start the connection again.',OWNERSHIP_STATE:'Your Desktop Pet’s ownership state changed. Close setup and try again.',RECOVERY_NOT_AUTHORIZED:'This Desktop Pet still contains another account’s link. Ask its previous owner to unlink it, or use the physical recovery process.',TICKET_INVALID:'Desktop Pet could not verify the account authorization. Close setup and try again.',INVALID_REQUEST:'Your Desktop Pet and this application use incompatible setup versions.',BACKEND_NOT_CONFIGURED:'This Desktop Pet does not have a setup service configured.'} as Record<string,string>)[code]??'Desktop Pet could not complete this setup step. Close setup and try again.';}
 private async pause(signal?:AbortSignal){signal?.throwIfAborted();await new Promise<void>((resolve,reject)=>{const abort=()=>{clearTimeout(timer);reject(new DOMException('Cancelled','AbortError'));};const timer=setTimeout(()=>{signal?.removeEventListener('abort',abort);resolve();},this.pollMs);signal?.addEventListener('abort',abort,{once:true});});}
 private async authorization(operation:'claim'|'network'|'reconcile',signal:AbortSignal){
  await this.command({op:'clock',utcMs:Date.now()});const issuedAt=Date.now();
  const signed=z.object({challenge:Challenge,proof:z.string().regex(/^[A-Za-z0-9_-]+$/).max(200)}).parse(await this.command({op:'challenge',operation,issuedAt}));
  if(signed.challenge.deviceId!==this.deviceId||signed.challenge.operation!==operation||signed.challenge.issuedAt!==issuedAt)throw new DomainError('DEVICE_IDENTITY_MISMATCH','This is not the Desktop Pet identified by your setup card.');
  const reply=z.object({ticket:z.string().min(1).max(4096),enrollmentId:z.string(),expiresAt:z.number()}).parse(await this.server.ticket(signed));
  try{signal.throwIfAborted();if(reply.expiresAt<=Date.now())throw new DomainError('SETUP_EXPIRED','Setup expired. Try again.');
   const ticket=new TextEncoder().encode(reply.ticket);if(ticket.length>4096)throw new DomainError('TICKET_TOO_LARGE','The setup ticket exceeds the supported size.');await this.command({op:'ticket_begin',length:ticket.length});
   for(let offset=0;offset<ticket.length;offset+=120){signal.throwIfAborted();const chunk=ticket.subarray(offset,offset+120);await this.command({op:'ticket_chunk',offset,hex:Array.from(chunk,n=>n.toString(16).padStart(2,'0')).join('')});}ticket.fill(0);
   const verified=await this.command({op:'ticket_finish'});if(verified.verified!==true)throw new DomainError('TICKET_REJECTED','Your Desktop Pet did not verify account authorization.');return reply.enrollmentId;
  }catch(error){await this.server.cancel(reply.enrollmentId).catch(()=>{});throw error;}
 }
 async reconcile(progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  signal.throwIfAborted();const status=await this.status();if(!status.linked)return false;const binding=await this.server.binding();
  if(binding){if(!binding.simulated&&binding.device_id===this.deviceId)return false;throw new DomainError('ACTIVE_BINDING','This Desktop Pet is linked to a different account or device record.');}
  if(!this.link.reconciliation)throw new DomainError('FIRMWARE_RECONCILIATION_REQUIRED','Your Desktop Pet was removed from your account, but this firmware cannot clear its previous link automatically. Update or physically reset your Desktop Pet, then start setup again.');
  progress('reconciling');let enrollmentId:string|undefined;
  try{enrollmentId=await this.authorization('reconcile',signal);const cleared=await this.command({op:'clear_owner'});if(cleared.cleared!==true)throw new DomainError('OWNERSHIP_STATE','Desktop Pet could not clear its previous account link.');await this.server.acknowledge(this.deviceId);return true;}
  catch(error){if(enrollmentId)await this.server.cancel(enrollmentId).catch(()=>{});throw error;}
 }
 async scan():Promise<ScanResult>{
  this.indices.clear();this.selected.clear();this.hidden.clear();await this.command({op:'clock',utcMs:Date.now()});await this.command({op:'scan'});const deadline=Date.now()+45000;let status;
  do{status=Status.parse(await this.command({op:'status'}));if(status.phase==='failed')throw new DomainError('SCAN_FAILED','Desktop Pet could not scan Wi-Fi. Try again.');if(status.phase==='scan_complete')break;await this.pause();}while(Date.now()<deadline);
  if(status.phase!=='scan_complete')throw new DomainError('SCAN_TIMEOUT','Your Desktop Pet took too long to scan Wi-Fi. Try again.');this.generation=status.scanGeneration;const scannedAt=Date.now(),networks:Network[]=[],byIdentity=new Map<string,Network>();
  for(let index=0;index<status.count;index++){
   const r=z.object({source:z.literal('robot'),ssid:z.string().max(128),rssi:z.number().min(-120).max(0),channel:z.number().int().min(1).max(14),scanGeneration:z.number().int(),supported:z.boolean(),security:z.enum(['open','wpa2-personal','wpa3-personal','unsupported'])}).parse(await this.command({op:'network',index}));
   if(r.scanGeneration!==this.generation)throw new DomainError('SCAN_CHANGED','Your Desktop Pet scanned again. Refresh the network list.');
   const identity=`${r.ssid}\0${r.security}`,existing=byIdentity.get(identity);if(existing){if(r.rssi>existing.rssi){existing.rssi=r.rssi;existing.channel=r.channel;existing.compatible=r.supported;this.indices.set(existing.id,index);}continue;}
   const network:Network={id:crypto.randomUUID(),ssid:r.ssid,rssi:r.rssi,channel:r.channel,security:r.security,compatible:r.supported};this.indices.set(network.id,index);this.selected.set(network.id,network);byIdentity.set(identity,network);networks.push(network);
  }
  networks.sort((a,b)=>b.rssi-a.rssi);return ScanResult.parse({v:1,source:'robot',deviceId:this.deviceId,scanId:crypto.randomUUID(),scannedAt,networks});
 }
 hiddenNetwork(ssid:string){const bytes=new TextEncoder().encode(ssid);if(!bytes.length||bytes.length>32)throw new DomainError('SSID_LENGTH','Use a hidden Wi-Fi name between 1 and 32 bytes.');if(ssid.includes('\0'))throw new DomainError('SSID_INVALID','The hidden Wi-Fi name contains an unsupported character.');const network:Network={id:crypto.randomUUID(),ssid,rssi:-120,channel:1,security:'wpa2-personal',compatible:true};this.selected.set(network.id,network);this.hidden.add(network.id);return network;}
 async connect(network:Network,password:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  const chosen=this.selected.get(network.id),index=this.indices.get(network.id),hidden=this.hidden.has(network.id);if(!chosen||(!hidden&&index===undefined)||chosen.ssid!==network.ssid||!chosen.compatible)throw new DomainError('NETWORK_NOT_SCANNED','Choose a supported network from your Desktop Pet’s scan.');
  const bytes=new TextEncoder().encode(password);if(chosen.security==='open'?bytes.length!==0:bytes.length<8||bytes.length>63)throw new DomainError('PASSWORD_LENGTH','Use the Wi-Fi password (8–63 bytes), or leave it blank for an open network.');bytes.fill(0);
  signal.throwIfAborted();const abort=()=>this.close();signal.addEventListener('abort',abort,{once:true});let enrollmentId:string|undefined;this.applied=false;
  try{
   enrollmentId=await this.authorization(this.change?'network':'claim',signal);
   signal.throwIfAborted();progress('connecting_wifi');this.applied=true;await this.command(hidden?{op:'apply_hidden',ssid:chosen.ssid,security:'wpa2-personal',password}:{op:'apply',index,scanGeneration:this.generation,password});password='';await this.waitForReady(chosen.ssid,progress,signal);
  }catch(error){if(enrollmentId&&!this.applied)await this.server.cancel(enrollmentId).catch(()=>{});throw error;}finally{password='';signal.removeEventListener('abort',abort);}
 }
 async resume(expectedNetwork:string|undefined,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){signal.throwIfAborted();const abort=()=>this.close();signal.addEventListener('abort',abort,{once:true});try{let status=await this.status();const network=expectedNetwork??status.pendingNetwork??status.savedNetwork??(await this.server.binding())?.network;if(!network)throw new DomainError('NO_PENDING_SETUP','No unfinished setup was found.');if(['scanning','connecting','checking_backend','linking','restoring_previous'].includes(status.phase)||(status.phase==='idle'&&status.linked&&status.networkConnected&&status.hasSavedNetwork&&!status.pendingNetwork&&status.savedNetwork===network)){await this.waitForReady(network,progress,signal);return;}await this.command({op:'clock',utcMs:Date.now()});status=await this.status();if(status.phase!=='network_verified')await this.command({op:'resume'});await this.waitForReady(network,progress,signal);}finally{signal.removeEventListener('abort',abort);}}
 private async waitForReady(network:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  const deadline=Date.now()+90000;
  do{signal.throwIfAborted();const status=await this.status();signal.throwIfAborted();
   if(status.phase==='failed'){if(!status.pendingNetwork)this.applied=false;throw new DomainError(status.error??'SETUP_FAILED',this.explanation(status.error??''));}if(status.phase==='awaiting_backend')throw new DomainError('SETUP_PENDING','Your Desktop Pet is waiting for server confirmation. Reconnect and resume setup; it may already be linked.');
   if(['checking_backend','linking'].includes(status.phase))progress('checking_server');
   if((status.phase==='network_verified'||(status.phase==='idle'&&!status.pendingNetwork&&status.savedNetwork===network))&&status.linked&&status.networkConnected&&status.hasSavedNetwork){const body=await this.server.binding();signal.throwIfAborted();if(body&&!body.simulated&&body.device_id===this.deviceId&&body.network===network){progress('ready');return;}throw new DomainError('STALE_LOCAL_LINK','Your Desktop Pet still contains an account link that was removed from the server. Close setup and connect again so it can be cleared safely.');}await this.pause(signal);
  }while(Date.now()<deadline);
  throw new DomainError('SETUP_PENDING','Setup is taking longer than expected. Reconnect to check it before starting over.');
 }
 close(){if(this.closed)return;this.closed=true;this.link.close();this.indices.clear();this.selected.clear();this.hidden.clear();}
}
