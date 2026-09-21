export const PROVISIONING_SERVICE='0000ff50-0000-1000-8000-00805f9b34fb';
export type ProvisioningStage='reconciling'|'connecting_wifi'|'checking_server'|'ready';
export interface ProvisioningTransport { readonly simulated:boolean; deviceId:string; }
export function provisioningSupport(){
 if(typeof window==='undefined')return {supported:false,reason:'Setup requires a browser.'};
 if(!window.isSecureContext)return {supported:false,reason:'Open Marvin over HTTPS or localhost to use Bluetooth.'};
 if(!('bluetooth' in navigator) || !navigator.bluetooth)return {supported:false,reason:'Bluetooth setup is not available in this browser. Use Chrome on a supported computer or Android device, signed in to this account.'};
 return {supported:true,reason:''};
}
