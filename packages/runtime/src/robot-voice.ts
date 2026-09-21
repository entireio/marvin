import type {ModelEvent,TextProvider,ToolExecutor,ModelContext} from './provider.js';
import type {VoiceConnection,VoiceProvider,VoiceSignal} from './voice.js';

const sampleRate=24000;
const carrierHz=42;
const delaySamples=47;
const highpassHz=85;
const lowpassHz=8000;
const highpassAlpha=(1/(2*Math.PI*highpassHz))/(1/(2*Math.PI*highpassHz)+1/sampleRate);
const lowpassAlpha=(1/sampleRate)/(1/(2*Math.PI*lowpassHz)+1/sampleRate);

/**
 * A deliberately restrained, streaming-safe robot treatment for Marvin's
 * 24 kHz mono PCM. Most of the dry voice survives; a quiet ring-modulated and
 * short comb-filtered layer supplies the metallic character.
 */
export class SubtleRobotVoice {
 private previousInput=0;private highpass=0;private lowpass=0;private phase=0;private cursor=0;private delay=new Float64Array(delaySamples);
 process(pcm:Uint8Array){
  if(pcm.byteLength%2)throw new Error('Robot voice requires s16le PCM.');
  const input=Buffer.from(pcm.buffer,pcm.byteOffset,pcm.byteLength),output=Buffer.allocUnsafe(input.length),phaseStep=2*Math.PI*carrierHz/sampleRate;
  for(let offset=0;offset<input.length;offset+=2){
   const value=input.readInt16LE(offset)/32768;
   this.highpass=highpassAlpha*(this.highpass+value-this.previousInput);this.previousInput=value;
   this.lowpass+=lowpassAlpha*(this.highpass-this.lowpass);
   const delayed=this.delay[this.cursor]!,quantized=Math.round(this.lowpass*1536)/1536,digital=this.lowpass*.82+quantized*.18;
   this.delay[this.cursor]=digital+delayed*.14;this.cursor=(this.cursor+1)%this.delay.length;
   const robot=this.lowpass*Math.sin(this.phase);this.phase+=phaseStep;if(this.phase>=2*Math.PI)this.phase-=2*Math.PI;
   const mixed=Math.max(-1,Math.min(1,digital*.86+delayed*.1+robot*.09));
   output.writeInt16LE(Math.round(mixed<0?mixed*32768:mixed*32767),offset);
  }
  return output;
 }
}

function roboticTextProvider(provider:TextProvider):TextProvider{
 return {name:provider.name,async *run(context:ModelContext,execute:ToolExecutor,signal:AbortSignal):AsyncIterable<ModelEvent>{
  const effect=new SubtleRobotVoice();
  for await(const event of provider.run(context,execute,signal))yield event.type==='audio'?{...event,pcm:effect.process(Buffer.from(event.pcm,'base64')).toString('base64')}:event;
 }};
}

/** Applies one identical voice signature to web, Pet, and direct-speech routes. */
export class RoboticVoiceProvider implements VoiceProvider {
 constructor(private provider:VoiceProvider){}
 async connect(notify:(signal:VoiceSignal)=>void):Promise<VoiceConnection>{
  const connection=await this.provider.connect(notify);
  const wrapped:VoiceConnection={append:pcm=>connection.append(pcm),clear:()=>connection.clear(),forTurn:itemId=>roboticTextProvider(connection.forTurn(itemId)),close:()=>connection.close()};
  if(connection.speak){const speak=connection.speak.bind(connection);wrapped.speak=async function*(text,signal){const effect=new SubtleRobotVoice();for await(const pcm of speak(text,signal))yield effect.process(pcm);};}
  return wrapped;
 }
}
