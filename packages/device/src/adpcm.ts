const INDEX_TABLE=[-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8] as const;
const STEP_TABLE=[7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767] as const;
export const ADPCM_MAGIC='MVA2',ADPCM_HEADER_BYTES=14,ADPCM_MAX_SAMPLES=960;
function clamp(value:number,min:number,max:number){return Math.max(min,Math.min(max,value));}
/** Decode one independently restartable IMA-ADPCM microphone frame. */
export function decodeAdpcmFrame(frame:Buffer){
 if(frame.length<ADPCM_HEADER_BYTES||frame.toString('ascii',0,4)!==ADPCM_MAGIC)throw new Error('Invalid ADPCM frame');
 const sequence=frame.readUInt32BE(4),samples=frame.readUInt16BE(8),predictor=frame.readInt16LE(10),initialIndex=frame[12]!;
 if(!samples||samples>ADPCM_MAX_SAMPLES||initialIndex>88||frame[13]!==0||frame.length!==ADPCM_HEADER_BYTES+Math.ceil((samples-1)/2))throw new Error('Invalid ADPCM frame');
 const pcm=Buffer.allocUnsafe(samples*2);let sample=predictor,index=initialIndex;pcm.writeInt16LE(sample,0);
 for(let i=1;i<samples;i++){
  const packed=frame[ADPCM_HEADER_BYTES+((i-1)>>1)]!,code=(i&1)?packed&15:packed>>4,step=STEP_TABLE[index]!;
  let difference=step>>3;if(code&1)difference+=step>>2;if(code&2)difference+=step>>1;if(code&4)difference+=step;
  sample=clamp(sample+(code&8?-difference:difference),-32768,32767);index=clamp(index+INDEX_TABLE[code]!,0,88);pcm.writeInt16LE(sample,i*2);
 }
 return {sequence,pcm};
}
