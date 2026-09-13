import {z} from 'zod';
import {DomainError,ScanResult,type Network} from '../../contracts/src/index.js';
import type {ProvisioningTransport,ProvisioningStage} from './index.js';
export interface SecureSetupLink {command(request:Record<string,unknown>):Promise<unknown>;close():void;}
export interface EnrollmentApi {
 ticket(request:{challenge:{deviceId:string;nonce:string;operation:'claim'|'network';issuedAt:number};proof:string}):Promise<{ticket:string;enrollmentId:string;expiresAt:number}>;
 cancel(id:string):Promise<void>;
 binding():Promise<null|{device_id:string;network:string;simulated:number}>;
}
const Status=z.object({phase:z.string(),linked:z.boolean(),networkConnected:z.boolean(),hasSavedNetwork:z.boolean(),scanGeneration:z.number().int().nonnegative(),count:z.number().int().min(0).max(32),error:z.string().optional(),pendingNetwork:z.string().max(128).optional(),savedNetwork:z.string().max(128).optional()});
const Reply=z.object({error:z.string().optional()}).passthrough();
const Challenge=z.object({deviceId:z.string(),nonce:z.string().regex(/^[a-f0-9]{64}$/),operation:z.enum(['claim','network']),issuedAt:z.number().int().positive()});
/** Owner-authorized protocol. Wi-Fi passwords only cross the encrypted BLE link. */
export class PhysicalRobot implements ProvisioningTransport {
 readonly simulated=false;private generation=-1;private indices=new Map<string,number>();private selected=new Map<string,Network>();private closed=false;private applied=false;
 constructor(readonly deviceId:string,private link:SecureSetupLink,private server:EnrollmentApi,private change=false,private pollMs=300){}
 private async command(request:Record<string,unknown>){if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to Marvin to continue.');const response=Reply.parse(await this.link.command(request));if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to check setup progress.');if(response.error)throw new DomainError(response.error,this.explanation(response.error));return response;}
 private explanation(code:string){return ({WIFI_CONNECTION_FAILED:'That network could not be reached. Check the Wi-Fi password and try again.',OWNER_TICKET_REQUIRED:'Setup authorization expired. Start the connection again.',SETUP_PENDING:'Marvin is confirming an earlier setup. Resume it before starting another.',ENROLLMENT_REVOKED:'This setup is no longer authorized. Start setup again.',CHALLENGE_EXPIRED:'Setup took too long. Start the connection again.'} as Record<string,string>)[code]??'Marvin could not complete this setup step. Reconnect and try again.';}
 private async pause(signal?:AbortSignal){signal?.throwIfAborted();await new Promise<void>((resolve,reject)=>{const abort=()=>{clearTimeout(timer);reject(new DOMException('Cancelled','AbortError'));};const timer=setTimeout(()=>{signal?.removeEventListener('abort',abort);resolve();},this.pollMs);signal?.addEventListener('abort',abort,{once:true});});}
 async scan():Promise<ScanResult>{
  this.indices.clear();this.selected.clear();await this.command({op:'clock',utcMs:Date.now()});await this.command({op:'scan'});const deadline=Date.now()+45000;let status;
  do{status=Status.parse(await this.command({op:'status'}));if(status.phase==='failed')throw new DomainError('SCAN_FAILED','Marvin could not scan Wi-Fi. Try again.');if(status.phase==='scan_complete')break;await this.pause();}while(Date.now()<deadline);
  if(status.phase!=='scan_complete')throw new DomainError('SCAN_TIMEOUT','Marvin took too long to scan Wi-Fi. Try again.');this.generation=status.scanGeneration;const networks:Network[]=[];
  for(let index=0;index<status.count;index++){
   const r=z.object({source:z.literal('robot'),ssid:z.string().max(128),rssi:z.number().min(-120).max(0),channel:z.number().int().min(1).max(14),scanGeneration:z.number().int(),supported:z.boolean(),security:z.enum(['open','wpa2-personal','wpa3-personal','unsupported'])}).parse(await this.command({op:'network',index}));
   if(r.scanGeneration!==this.generation)throw new DomainError('SCAN_CHANGED','Marvin scanned again. Refresh the network list.');
   const network:Network={id:crypto.randomUUID(),ssid:r.ssid,rssi:r.rssi,channel:r.channel,security:r.security,compatible:r.supported};this.indices.set(network.id,index);this.selected.set(network.id,network);networks.push(network);
  }
  return ScanResult.parse({v:1,source:'robot',deviceId:this.deviceId,scanId:crypto.randomUUID(),scannedAt:Date.now(),networks});
 }
 async connect(network:Network,password:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  const chosen=this.selected.get(network.id),index=this.indices.get(network.id);if(!chosen||index===undefined||chosen.ssid!==network.ssid||!chosen.compatible)throw new DomainError('NETWORK_NOT_SCANNED','Choose a supported network from Marvin’s latest scan.');
  const bytes=new TextEncoder().encode(password);if(chosen.security==='open'?bytes.length!==0:bytes.length<8||bytes.length>63)throw new DomainError('PASSWORD_LENGTH','Use the Wi-Fi password (8–63 bytes), or leave it blank for an open network.');bytes.fill(0);
  signal.throwIfAborted();const abort=()=>this.close();signal.addEventListener('abort',abort,{once:true});let enrollmentId:string|undefined;this.applied=false;
  try{
   await this.command({op:'clock',utcMs:Date.now()});const issuedAt=Date.now(),operation=this.change?'network':'claim';const signed=z.object({challenge:Challenge,proof:z.string().regex(/^[A-Za-z0-9_-]+$/).max(200)}).parse(await this.command({op:'challenge',operation,issuedAt}));
   if(signed.challenge.deviceId!==this.deviceId||signed.challenge.operation!==operation||signed.challenge.issuedAt!==issuedAt)throw new DomainError('DEVICE_IDENTITY_MISMATCH','This is not the Marvin identified by your setup card.');
   const reply=z.object({ticket:z.string().min(1).max(4096),enrollmentId:z.string(),expiresAt:z.number()}).parse(await this.server.ticket(signed));enrollmentId=reply.enrollmentId;signal.throwIfAborted();if(reply.expiresAt<=Date.now())throw new DomainError('SETUP_EXPIRED','Setup expired. Try again.');
   const ticket=new TextEncoder().encode(reply.ticket);if(ticket.length>4096)throw new DomainError('TICKET_TOO_LARGE','The setup ticket exceeds the supported size.');await this.command({op:'ticket_begin',length:ticket.length});
   for(let offset=0;offset<ticket.length;offset+=120){signal.throwIfAborted();const chunk=ticket.subarray(offset,offset+120);await this.command({op:'ticket_chunk',offset,hex:Array.from(chunk,n=>n.toString(16).padStart(2,'0')).join('')});}ticket.fill(0);
   const verified=await this.command({op:'ticket_finish'});if(verified.verified!==true)throw new DomainError('TICKET_REJECTED','Marvin did not verify account authorization.');
   signal.throwIfAborted();progress('connecting_wifi');this.applied=true;await this.command({op:'apply',index,scanGeneration:this.generation,password});password='';await this.waitForReady(chosen.ssid,progress,signal);
  }catch(error){if(enrollmentId&&!this.applied)await this.server.cancel(enrollmentId).catch(()=>{});throw error;}finally{password='';signal.removeEventListener('abort',abort);}
 }
 async resume(expectedNetwork:string|undefined,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){signal.throwIfAborted();const abort=()=>this.close();signal.addEventListener('abort',abort,{once:true});try{await this.command({op:'clock',utcMs:Date.now()});const status=Status.parse(await this.link.command({op:'status'}));const network=expectedNetwork??status.pendingNetwork??status.savedNetwork??(await this.server.binding())?.network;if(!network)throw new DomainError('NO_PENDING_SETUP','No pending network was reported. Start setup again.');if(status.phase!=='network_verified'&&!(status.phase==='idle'&&status.linked&&status.networkConnected&&status.hasSavedNetwork&&!status.pendingNetwork&&status.savedNetwork===network))await this.command({op:'resume'});await this.waitForReady(network,progress,signal);}finally{signal.removeEventListener('abort',abort);}}
 private async waitForReady(network:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  const deadline=Date.now()+90000;
  do{signal.throwIfAborted();const raw=await this.link.command({op:'status'});signal.throwIfAborted();if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to check setup progress.');const status=Status.parse(raw);
   if(status.phase==='failed'){if(!status.pendingNetwork)this.applied=false;throw new DomainError(status.error??'SETUP_FAILED',this.explanation(status.error??''));}if(status.phase==='awaiting_backend')throw new DomainError('SETUP_PENDING','Marvin is waiting for server confirmation. Reconnect and resume setup; it may already be linked.');
   if(['checking_backend','linking'].includes(status.phase))progress('checking_server');
   if((status.phase==='network_verified'||(status.phase==='idle'&&!status.pendingNetwork&&status.savedNetwork===network))&&status.linked&&status.networkConnected&&status.hasSavedNetwork){const body=await this.server.binding();signal.throwIfAborted();if(body&&!body.simulated&&body.device_id===this.deviceId&&body.network===network){progress('ready');return;}throw new DomainError('BINDING_UNCONFIRMED','The server has not confirmed this account link. Reconnect to check it.');}await this.pause(signal);
  }while(Date.now()<deadline);
  throw new DomainError('SETUP_PENDING','Setup is taking longer than expected. Reconnect to check it before starting over.');
 }
 close(){if(this.closed)return;this.closed=true;this.link.close();this.indices.clear();this.selected.clear();}
}
