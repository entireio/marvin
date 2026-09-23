import {describe,expect,it} from 'vitest';
import {decodeAdpcmFrame} from '../../packages/device/src/adpcm.js';

describe('IMA-ADPCM microphone frames',()=>{
 it('decodes an independently restartable silence frame',()=>{
  const frame=Buffer.alloc(14+5);frame.write('MVA2');frame.writeUInt32BE(42,4);frame.writeUInt16BE(10,8);frame.writeInt16LE(0,10);
  const decoded=decodeAdpcmFrame(frame);expect(decoded.sequence).toBe(42);expect([...new Int16Array(decoded.pcm.buffer,decoded.pcm.byteOffset,10)]).toEqual(Array(10).fill(0));
 });
 it('rejects malformed and oversized frames',()=>{
  expect(()=>decodeAdpcmFrame(Buffer.from('MVA2'))).toThrow();
  const frame=Buffer.alloc(14);frame.write('MVA2');frame.writeUInt16BE(961,8);expect(()=>decodeAdpcmFrame(frame)).toThrow();
 });
});
