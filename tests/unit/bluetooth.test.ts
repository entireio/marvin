import {it,expect} from 'vitest';
import {BluetoothSetupSession,bluetoothError,type BluetoothDevice,type GattCharacteristic,type GattServer} from '../../packages/provisioning/src/bluetooth.js';
function fixture(version:object,stall=false){
 let disconnects=0,writes=0,listeners=0;const bytes=new TextEncoder().encode(JSON.stringify(version));
 const endpoint:GattCharacteristic={async writeValueWithResponse(){writes++;},async readValue(){if(stall)return new Promise<DataView>(()=>{});return new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);}};
 const server:GattServer={connected:true,async connect(){return server;},disconnect(){disconnects++;},async getPrimaryService(){return {async getCharacteristic(){return endpoint;}};}};
 const device:BluetoothDevice={gatt:server,addEventListener(){listeners++;},removeEventListener(){listeners--;}};
 return {device,get writes(){return writes;},get disconnects(){return disconnects;},get listeners(){return listeners;}};
}
it('rejects insecure or incompatible firmware before exchanging setup credentials',async()=>{
 for(const version of [{marvin:1,security:0,patch:1},{marvin:1,security:2,patch:0},{marvin:2,security:2,patch:1}]){
  const f=fixture(version);await expect(BluetoothSetupSession.open(f.device,'fixture-user','fixture-secret')).rejects.toMatchObject({code:'FIRMWARE_INCOMPATIBLE'});expect(f.writes).toBe(1);expect(f.disconnects).toBe(1);expect(f.listeners).toBe(0);
 }
});
it('times out a stalled GATT operation, disconnects and releases event handlers',async()=>{
 const f=fixture({},true);await expect(BluetoothSetupSession.open(f.device,'fixture-user','fixture-secret',15)).rejects.toMatchObject({code:'BLUETOOTH_TIMEOUT'});expect(f.disconnects).toBe(1);expect(f.listeners).toBe(0);
});
it('missing GATT support never starts a credential exchange',async()=>{
 const device:BluetoothDevice={addEventListener(){},removeEventListener(){}};await expect(BluetoothSetupSession.open(device,'fixture-user','fixture-secret')).rejects.toMatchObject({code:'BLUETOOTH_UNAVAILABLE'});
});

it('disconnects a connection that completes after the setup deadline',async()=>{
 const f=fixture({});let finish:(server:GattServer)=>void=()=>{};const server=f.device.gatt!;server.connect=()=>new Promise(resolve=>{finish=resolve;});
 await expect(BluetoothSetupSession.open(f.device,'fixture-user','fixture-secret',15)).rejects.toMatchObject({code:'BLUETOOTH_TIMEOUT'});
 finish(server);await new Promise(resolve=>setTimeout(resolve,0));expect(f.disconnects).toBe(2);expect(f.writes).toBe(0);expect(f.listeners).toBe(0);
});
it('treats dismissal of the browser chooser as a silent cancellation',()=>{
 const error=bluetoothError(new DOMException('User cancelled the requestDevice() chooser.','NotFoundError'));expect(error).toMatchObject({code:'BLUETOOTH_CANCELLED',message:''});
});
