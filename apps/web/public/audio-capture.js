/* PCM s16le, 24 kHz, 20 ms frames. Runs on the audio thread, independent of UI rendering. */
class MarvinCapture extends AudioWorkletProcessor {
 constructor(){super();this.frame=new Int16Array(480);this.at=0;this.energy=0;this.hot=0;this.quiet=0;this.speaking=false;this.muted=false;this.port.onmessage=e=>{this.muted=!!e.data.muted;this.at=0;this.energy=0;this.hot=0;this.speaking=false;};}
 process(inputs){const input=inputs[0]?.[0];if(!input||this.muted)return true;for(const sample of input){const v=Math.max(-1,Math.min(1,sample));this.frame[this.at++]=v<0?v*32768:v*32767;this.energy+=v*v;if(this.at===480){const loud=Math.sqrt(this.energy/480)>.025;this.hot=loud?this.hot+1:0;this.quiet=loud?0:this.quiet+1;if(!this.speaking&&this.hot>=2){this.speaking=true;this.port.postMessage({type:'speech'});}if(this.quiet>=15)this.speaking=false;const bytes=this.frame.buffer;this.port.postMessage({type:'pcm',bytes},[bytes]);this.frame=new Int16Array(480);this.at=0;this.energy=0;}}return true;}
}
registerProcessor('marvin-capture',MarvinCapture);
