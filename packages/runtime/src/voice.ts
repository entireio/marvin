import type { TextProvider } from './provider.js';
/** Provider protocols and credentials never cross this boundary. PCM is mono s16le at 24 kHz. */
export type VoiceSignal={type:'speech_start'|'speech_end'}|{type:'transcript';itemId:string;text:string}|{type:'fault';code:string};
export interface VoiceConnection {
 append(pcm:Uint8Array):void;
 clear():void;
 forTurn(itemId:string):TextProvider;
 close():void;
}
export interface VoiceProvider {connect(notify:(signal:VoiceSignal)=>void):Promise<VoiceConnection>}
