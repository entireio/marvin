#include "audio_rate.h"
#include <math.h>
#include <string.h>
void marvin_rate_init(marvin_rate_t *r,bool input_16k){
 memset(r,0,sizeof(*r));r->up=input_16k?3:2;r->down=input_16k?2:3;
 const double pi=3.141592653589793,cutoff=7000.0/48000.0;double sum=0;
 for(int i=0;i<63;i++){int x=i-31;double sinc=x?sin(2*pi*cutoff*x)/(pi*x):2*cutoff;double window=.42-.5*cos(2*pi*i/62)+.08*cos(4*pi*i/62);r->coefficients[i]=(float)(sinc*window);sum+=r->coefficients[i];}
 for(int i=0;i<63;i++)r->coefficients[i]=(float)(r->coefficients[i]*r->up/sum);
}
size_t marvin_rate_convert(marvin_rate_t *r,const int16_t *input,size_t count,int16_t *output,size_t capacity){
 if(!r||!input||!output||!r->up||!r->down||count>SIZE_MAX/r->up)return 0;
 size_t ticks=count*r->up,required=ticks/r->down+((ticks%r->down+r->phase)>=r->down?1:0);
 if(capacity<required)return 0;
 size_t used=0;
 for(size_t n=0;n<count;n++)for(unsigned u=0;u<r->up;u++){
  r->history[r->position]=u?0:input[n];
  if(++r->phase==r->down){float value=0;unsigned p=r->position;r->phase=0;
   for(int k=0;k<63;k++){value+=r->coefficients[k]*r->history[p];p=p?p-1:62;}
   /* Keep per-sample clipping/rounding in the hardware float path. */
   if(value>32767)value=32767;else if(value< -32768)value=-32768;
   output[used++]=(int16_t)(value>=0?value+.5f:value-.5f);
  }
  r->position=(r->position+1)%63;
 }
 return used;
}
