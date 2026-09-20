/**
 * Bounded, transport-neutral framing for the local BLE bridge.  GATT does not
 * preserve an application message boundary, whereas the existing device
 * protocol relies on one.  This codec deliberately carries the same JSON and
 * PCM payloads as the WebSocket device link; authentication lives above it.
 */
export const BLE_FRAME_MAGIC=0x4d424c31; // "MBL1"
export const BLE_ATT_PAYLOAD=240;
export const BLE_FRAME_HEADER=12;
export const BLE_FRAGMENT_PAYLOAD=BLE_ATT_PAYLOAD-BLE_FRAME_HEADER;
export const BLE_MAX_MESSAGE=4096;
export type BleFrameKind='control'|'audio';
const kinds:Record<BleFrameKind,number>={control:1,audio:2};
const reverse:Record<number,BleFrameKind|undefined>={1:'control',2:'audio'};
export type BleMessage={kind:BleFrameKind;sequence:number;payload:Uint8Array};

export function fragmentBleMessage(kind:BleFrameKind,sequence:number,payload:Uint8Array){
 if(!Number.isInteger(sequence)||sequence<0||sequence>0xffff)throw new Error('BLE sequence is invalid');
 if(!payload.length||payload.length>BLE_MAX_MESSAGE)throw new Error('BLE message size is invalid');
 const total=Math.ceil(payload.length/BLE_FRAGMENT_PAYLOAD),frames:Uint8Array[]=[];
 for(let part=0;part<total;part++){
  const start=part*BLE_FRAGMENT_PAYLOAD,body=payload.subarray(start,start+BLE_FRAGMENT_PAYLOAD),frame=new Uint8Array(BLE_FRAME_HEADER+body.length),view=new DataView(frame.buffer);
  view.setUint32(0,BLE_FRAME_MAGIC);view.setUint8(4,kinds[kind]);view.setUint16(5,sequence);view.setUint8(7,part);view.setUint8(8,total);view.setUint16(9,payload.length);view.setUint8(11,0);frame.set(body,BLE_FRAME_HEADER);frames.push(frame);
 }
 return frames;
}

type Pending={kind:BleFrameKind;sequence:number;total:number;size:number;parts:Uint8Array[];received:number;createdAt:number};
/** Rejects reordered, duplicate and stale fragments rather than guessing. */
export class BleFrameDecoder {
 private pending?:Pending;
 constructor(private readonly now=()=>Date.now(),private readonly timeoutMs=2000){}
 append(frame:Uint8Array):BleMessage|undefined{
  if(frame.length<BLE_FRAME_HEADER||frame.length>BLE_ATT_PAYLOAD)throw new Error('BLE fragment size is invalid');
  const view=new DataView(frame.buffer,frame.byteOffset,frame.byteLength),kind=reverse[view.getUint8(4)],sequence=view.getUint16(5),part=view.getUint8(7),total=view.getUint8(8),size=view.getUint16(9);
  if(view.getUint32(0)!==BLE_FRAME_MAGIC||!kind||!total||part>=total||!size||size>BLE_MAX_MESSAGE||view.getUint8(11)!==0)throw new Error('BLE fragment header is invalid');
  if(this.pending&&this.now()-this.pending.createdAt>this.timeoutMs)this.pending=undefined;
  if(!this.pending){if(part!==0)throw new Error('BLE message must start at fragment zero');this.pending={kind,sequence,total,size,parts:[],received:0,createdAt:this.now()};}
  const pending=this.pending;
  if(pending.kind!==kind||pending.sequence!==sequence||pending.total!==total||pending.size!==size||part!==pending.received)throw new Error('BLE fragments are out of sequence');
  const body=frame.subarray(BLE_FRAME_HEADER);if(!body.length||pending.received*BLE_FRAGMENT_PAYLOAD+body.length>size)throw new Error('BLE fragment payload is invalid');
  pending.parts.push(body);pending.received++;
  if(pending.received!==total)return;
  const payload=new Uint8Array(size);let offset=0;for(const chunk of pending.parts){payload.set(chunk,offset);offset+=chunk.length;}if(offset!==size)throw new Error('BLE message is incomplete');
  this.pending=undefined;return {kind,sequence,payload};
 }
 reset(){this.pending=undefined;}
}
