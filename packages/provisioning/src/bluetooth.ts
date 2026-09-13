import {DomainError} from '../../contracts/src/index.js';
import {Security2} from './security2.js';
import {PROVISIONING_SERVICE,provisioningSupport} from './index.js';
export interface GattCharacteristic {writeValueWithResponse(value:Uint8Array<ArrayBuffer>):Promise<void>;readValue():Promise<DataView>;}
export interface GattServer {connected:boolean;connect():Promise<GattServer>;disconnect():void;getPrimaryService(uuid:string):Promise<{getCharacteristic(uuid:string):Promise<GattCharacteristic>}>;}
export interface BluetoothDevice {gatt?:GattServer;addEventListener(type:'gattserverdisconnected',listener:()=>void):void;removeEventListener(type:'gattserverdisconnected',listener:()=>void):void;}
/** One selected robot and one secure session. Never persists setup secrets or network data. */
export class BluetoothSetupSession {
 private secure=new Security2();private endpoints=new Map<string,GattCharacteristic>();private closed=false;private pending=false;private cancel=new AbortController();
 private onDisconnect=()=>this.close();
 private constructor(private device:BluetoothDevice,private timeoutMs:number){device.addEventListener('gattserverdisconnected',this.onDisconnect);}
 static async open(device:BluetoothDevice,username:string,secret:string,timeoutMs=20000,requireEnrollment=false){
  const session=new BluetoothSetupSession(device,timeoutMs);
  try{
   if(!device.gatt)throw new DomainError('BLUETOOTH_UNAVAILABLE','Bluetooth connection is unavailable.');
   const connected=await session.bounded(device.gatt.connect().then(server=>{if(session.closed){server.disconnect();throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to Marvin to continue setup.');}return server;}));
   const service=await session.bounded(connected.getPrimaryService(PROVISIONING_SERVICE));
   for(const suffix of ['ff51','ff52','ff53'])session.endpoints.set(suffix,await session.bounded(service.getCharacteristic(PROVISIONING_SERVICE.replace('ff50',suffix))));
   const version=JSON.parse(new TextDecoder().decode(await session.exchange('ff51',new TextEncoder().encode('---'))));
   if(version.marvin!==1||version.security!==2||version.patch!==1)throw new DomainError('FIRMWARE_INCOMPATIBLE','Marvin needs compatible secure setup firmware.');
   if(requireEnrollment&&version.enrollment!==1)throw new DomainError('FIRMWARE_INCOMPATIBLE','Marvin needs firmware that supports account linking.');
   await session.secure.open(username,secret,bytes=>session.exchange('ff52',bytes));
   if(session.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to Marvin to continue setup.');
   return session;
  }catch(error){session.close();throw error;}
 }
 private async bounded<T>(operation:Promise<T>):Promise<T>{
  this.cancel.signal.throwIfAborted();let timer:ReturnType<typeof setTimeout>|undefined,abort=()=>{};
  try{return await Promise.race([operation,new Promise<never>((_,reject)=>{abort=()=>reject(new DomainError('BLUETOOTH_DISCONNECTED','Bluetooth disconnected. Reconnect to check setup progress.'));this.cancel.signal.addEventListener('abort',abort,{once:true});timer=setTimeout(()=>{reject(new DomainError('BLUETOOTH_TIMEOUT','Marvin did not respond. Reconnect to check setup progress.'));this.close();},this.timeoutMs);})]);}
  finally{clearTimeout(timer);this.cancel.signal.removeEventListener('abort',abort);}
 }
 private async exchange(channel:string,bytes:Uint8Array){
  if(this.closed||this.pending)throw new DomainError('BLUETOOTH_BUSY','Wait for the current Bluetooth operation or reconnect.');
  const endpoint=this.endpoints.get(channel);if(!endpoint)throw new DomainError('FIRMWARE_INCOMPATIBLE','Marvin is missing a required setup endpoint.');
  this.pending=true;
  try{await this.bounded(endpoint.writeValueWithResponse(new Uint8Array(bytes)));const view=await this.bounded(endpoint.readValue());return new Uint8Array(view.buffer.slice(view.byteOffset,view.byteOffset+view.byteLength));}
  catch(error){this.close();throw error;}finally{this.pending=false;}
 }
 async command(request:Record<string,unknown>):Promise<unknown>{
  if(this.closed)throw new DomainError('BLUETOOTH_DISCONNECTED','Reconnect to Marvin to continue setup.');
  const bytes=new TextEncoder().encode(JSON.stringify(request));if(bytes.length>384)throw new DomainError('SETUP_MESSAGE_TOO_LARGE','Setup data must use the bounded transfer protocol.');
  try{const response=await this.secure.exchange(bytes,data=>this.exchange('ff53',data));return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(response));}
  catch(error){this.close();throw error;}finally{bytes.fill(0);}
 }
 close(){if(this.closed)return;this.closed=true;this.secure.close();this.cancel.abort();this.device.removeEventListener('gattserverdisconnected',this.onDisconnect);this.device.gatt?.disconnect();this.endpoints.clear();}
}
/** Call directly from the setup button's user gesture. No secrets are requested here. */
export async function chooseBluetoothRobot():Promise<BluetoothDevice>{
 const support=provisioningSupport();if(!support.supported)throw new DomainError('BROWSER_UNSUPPORTED',support.reason);
 const bluetooth=(navigator as unknown as {bluetooth:{requestDevice(options:unknown):Promise<BluetoothDevice>}}).bluetooth;
 return bluetooth.requestDevice({filters:[{services:[PROVISIONING_SERVICE]}]});
}
