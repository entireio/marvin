import {describe,it,expect} from 'vitest';
import {Pcm16To24} from '../../packages/device/src/pcm-rate.js';
describe('native device PCM conversion',()=>{
 it('preserves duration and chunk continuity',()=>{
  const input=Buffer.alloc(3200);for(let i=0;i<1600;i++)input.writeInt16LE(Math.round(10000*Math.sin(i*2*Math.PI*1000/16000)),i*2);
  const whole=new Pcm16To24().convert(input),stream=new Pcm16To24(),parts:Buffer[]=[];
  for(let i=0;i<input.length;i+=74)parts.push(stream.convert(input.subarray(i,i+74)));
  expect(whole.length).toBe(4800);expect(Buffer.concat(parts)).toEqual(whole);
  let power=0;for(let i=100;i<2400;i++)power+=whole.readInt16LE(i*2)**2;
  expect(Math.sqrt(power/2300)).toBeGreaterThan(6900);expect(Math.sqrt(power/2300)).toBeLessThan(7200);
 });
 it('rejects malformed input without changing state and clears history',()=>{
  const a=new Pcm16To24(),b=new Pcm16To24(),input=Buffer.alloc(320,30);
  expect(a.convert(input)).toEqual(b.convert(input));
  for(const size of [0,1,4098])expect(()=>a.convert(Buffer.alloc(size))).toThrow();
  expect(a.convert(input)).toEqual(b.convert(input));a.clear();expect(a.convert(input)).toEqual(new Pcm16To24().convert(input));
 });
});
