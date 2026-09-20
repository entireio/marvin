import { expect,it } from 'vitest';
import { BLE_ATT_PAYLOAD,BleFrameDecoder,fragmentBleMessage } from '../../packages/device/src/ble-frame.js';

it('round-trips bounded control and PCM messages through ATT-sized fragments',()=>{
 for(const [kind,size] of [['control',377],['audio',4096]] as const){
  const payload=Uint8Array.from({length:size},(_,i)=>i%251),frames=fragmentBleMessage(kind,42,payload),decoder=new BleFrameDecoder();
  expect(frames.every(frame=>frame.length<=BLE_ATT_PAYLOAD)).toBe(true);let message;
  for(const frame of frames)message=decoder.append(frame)??message;
  expect(message).toMatchObject({kind,sequence:42});expect(message?.payload).toEqual(payload);
 }
});
it('rejects oversized, reordered and stale BLE messages',()=>{
 expect(()=>fragmentBleMessage('audio',1,new Uint8Array(4097))).toThrow();
 const frames=fragmentBleMessage('audio',2,new Uint8Array(500));
 expect(()=>new BleFrameDecoder().append(frames[1]!)).toThrow('fragment zero');
 const decoder=new BleFrameDecoder();decoder.append(frames[0]!);expect(()=>decoder.append(frames[0]!)).toThrow('out of sequence');
 let clock=0;const stale=new BleFrameDecoder(()=>clock,10);stale.append(frames[0]!);clock=11;expect(()=>stale.append(frames[1]!)).toThrow('fragment zero');
});
