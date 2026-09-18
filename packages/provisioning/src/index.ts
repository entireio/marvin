import { ScanResult, DomainError, type Network } from '../../contracts/src/index.js';
export const PROVISIONING_SERVICE='0000ff50-0000-1000-8000-00805f9b34fb';
export type ProvisioningStage='reconciling'|'connecting_wifi'|'checking_server'|'ready';
export interface ProvisioningTransport { readonly simulated:boolean; deviceId:string; scan():Promise<ScanResult>; connect(network:Network,password:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal):Promise<void>; }
export function provisioningSupport(){
 if(typeof window==='undefined')return {supported:false,reason:'Setup requires a browser.'};
 if(!window.isSecureContext)return {supported:false,reason:'Open Marvin over HTTPS or localhost to use Bluetooth.'};
 if(!('bluetooth' in navigator) || !navigator.bluetooth)return {supported:false,reason:'Bluetooth setup is not available in this browser. Use Chrome on a supported computer or Android device, signed in to this account.'};
 return {supported:true,reason:''};
}
export async function discoverPhysicalRobot(){
 const support=provisioningSupport();if(!support.supported)throw new DomainError('BROWSER_UNSUPPORTED',support.reason);
 const bluetooth=(navigator as unknown as {bluetooth:{requestDevice(options:unknown):Promise<{name?:string;gatt?:{connect():Promise<{disconnect():void}>}}>}}).bluetooth;
 const device=await bluetooth.requestDevice({filters:[{services:[PROVISIONING_SERVICE]}]});
 if(!device.gatt)throw new DomainError('BLUETOOTH_UNAVAILABLE','Bluetooth connection is unavailable.');
 const server=await device.gatt.connect();server.disconnect();
 // Fail closed: never send passwords before the Security 2 implementation is validated on hardware.
 throw new DomainError('SECURE_SETUP_PENDING',`${device.name??'Desktop Pet'} was found. Secure hardware provisioning is not enabled in this M0–M3 build. No settings were changed.`);
}
export class SimulatedRobot implements ProvisioningTransport {
 readonly simulated=true;deviceId='simulated-marvin-001';committedNetwork:string|null=null;
 async scan(){return ScanResult.parse({v:1,source:'robot',deviceId:this.deviceId,scanId:crypto.randomUUID(),scannedAt:Date.now(),networks:[{id:'studio',ssid:'Studio',rssi:-46,channel:6,security:'wpa2-personal',compatible:true},{id:'home',ssid:'Home Wi-Fi',rssi:-64,channel:1,security:'wpa2-personal',compatible:true},{id:'guest',ssid:'Guest network',rssi:-78,channel:11,security:'wpa2-personal',compatible:true}]});}
 async connect(network:Network,password:string,progress:(stage:ProvisioningStage)=>void,signal:AbortSignal){
  if(!network.compatible)throw new DomainError('NETWORK_UNSUPPORTED','Your Desktop Pet cannot join this network.');
  if(network.security!=='open'&&(password.length<8||password.length>63))throw new DomainError('PASSWORD_LENGTH','Use the Wi-Fi password (8–63 characters).');
  const pause=()=>new Promise<void>((resolve,reject)=>{signal.throwIfAborted();const abort=()=>{clearTimeout(timer);reject(new DOMException('Cancelled','AbortError'));};const timer=setTimeout(()=>{signal.removeEventListener('abort',abort);resolve();},550);signal.addEventListener('abort',abort,{once:true});});
  progress('connecting_wifi');await pause();if(password==='wrong-password')throw new DomainError('WIFI_AUTH_FAILED','That password did not work. Check it and try again.');
  progress('checking_server');await pause();signal.throwIfAborted();this.committedNetwork=network.ssid;progress('ready');
 }
}
