#include "audio_adpcm.h"
#include <string.h>
static const int8_t index_table[16]={-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8};
static const int16_t step_table[89]={7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
static int clamp(int value,int low,int high){return value<low?low:value>high?high:value;}
size_t marvin_adpcm_encode(uint32_t sequence,const int16_t *pcm,size_t samples,uint8_t *output,size_t capacity){
 size_t bytes=MARVIN_ADPCM_HEADER_BYTES+(samples?((samples-1)+1)/2:0);if(!pcm||!output||!samples||samples>MARVIN_ADPCM_MAX_SAMPLES||capacity<bytes)return 0;
 memcpy(output,"MVA2",4);output[4]=sequence>>24;output[5]=sequence>>16;output[6]=sequence>>8;output[7]=sequence;output[8]=samples>>8;output[9]=samples;
 int predictor=pcm[0],index=0;output[10]=predictor;output[11]=predictor>>8;output[12]=0;output[13]=0;memset(output+14,0,bytes-14);
 for(size_t i=1;i<samples;i++){
  int step=step_table[index],difference=(int)pcm[i]-predictor,code=0;if(difference<0){code=8;difference=-difference;}
  int delta=step>>3;if(difference>=step){code|=4;difference-=step;delta+=step;}if(difference>=step>>1){code|=2;difference-=step>>1;delta+=step>>1;}if(difference>=step>>2){code|=1;delta+=step>>2;}
  predictor=clamp(predictor+(code&8?-delta:delta),-32768,32767);index=clamp(index+index_table[code],0,88);
  size_t at=MARVIN_ADPCM_HEADER_BYTES+((i-1)>>1);if(i&1)output[at]=(uint8_t)code;else output[at]|=(uint8_t)(code<<4);
 }
 return bytes;
}
