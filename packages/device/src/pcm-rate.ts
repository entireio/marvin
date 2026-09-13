/** Streaming16→24-kHz PCM converter.63-tap7-kHz low-pass at the shared48-kHz
 * rate; persistent phase/history make arbitrary transport chunking transparent. */
export class Pcm16To24 {
 private history=new Float64Array(63);private coefficients=new Float64Array(63);
 private position=0;private phase=0;
 constructor(){let sum=0;const cutoff=7000/48000;
  for(let i=0;i<63;i++){const x=i-31,sinc=x?Math.sin(2*Math.PI*cutoff*x)/(Math.PI*x):2*cutoff,window=.42-.5*Math.cos(2*Math.PI*i/62)+.08*Math.cos(4*Math.PI*i/62);this.coefficients[i]=sinc*window;sum+=this.coefficients[i];}
  for(let i=0;i<63;i++)this.coefficients[i]=this.coefficients[i]*3/sum;
 }
 convert(input:Buffer):Buffer{
  if(!input.length||input.length%2||input.length>4096)throw new Error('Invalid device PCM frame');
  const ticks=input.length/2*3,needed=Math.floor((ticks+this.phase)/2),out=Buffer.alloc(needed*2);let used=0;
  for(let n=0;n<input.length;n+=2)for(let u=0;u<3;u++){
   this.history[this.position]=u===0?input.readInt16LE(n):0;
   if(++this.phase===2){let value=0,p=this.position;this.phase=0;
    for(let k=0;k<63;k++){value+=this.coefficients[k]*this.history[p];p=p?p-1:62;}
    out.writeInt16LE(Math.max(-32768,Math.min(32767,Math.round(value))),used*2);used++;
   }
   this.position=(this.position+1)%63;
  }
  return out;
 }
 clear(){this.history.fill(0);this.position=this.phase=0;}
}
