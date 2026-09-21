import {describe,expect,it} from 'vitest';
import {SubtleRobotVoice} from '../../packages/runtime/src/robot-voice.js';

const tone=(samples:number)=>{const pcm=Buffer.alloc(samples*2);for(let i=0;i<samples;i++)pcm.writeInt16LE(Math.round(Math.sin(2*Math.PI*220*i/24000)*12000),i*2);return pcm;};

describe('subtle robot voice',()=>{
 it('is streaming-safe and changes voiced audio without clipping',()=>{
  const input=tone(2400),whole=new SubtleRobotVoice().process(input),streamedEffect=new SubtleRobotVoice(),streamed=Buffer.concat([streamedEffect.process(input.subarray(0,960)),streamedEffect.process(input.subarray(960,2500)),streamedEffect.process(input.subarray(2500))]);
  expect(streamed).toEqual(whole);expect(whole.equals(input)).toBe(false);
  let peak=0;for(let i=0;i<whole.length;i+=2)peak=Math.max(peak,Math.abs(whole.readInt16LE(i)));expect(peak).toBeGreaterThan(5000);expect(peak).toBeLessThanOrEqual(32767);
 });
 it('keeps silence silent and rejects malformed PCM',()=>{const effect=new SubtleRobotVoice();expect(effect.process(Buffer.alloc(960))).toEqual(Buffer.alloc(960));expect(()=>effect.process(Buffer.alloc(3))).toThrow(/s16le/);});
});
