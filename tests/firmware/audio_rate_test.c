#include "audio_rate.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <string.h>
static int16_t input[24000],whole[36000],chunks[36000];
static double rms(const int16_t *samples,size_t n){double sum=0;for(size_t i=300;i<n;i++)sum+=(double)samples[i]*samples[i];return sqrt(sum/(n-300));}
int main(void){
 for(int direction=0;direction<2;direction++){
  const int rate=direction?16000:24000;marvin_rate_t a,b;marvin_rate_init(&a,direction);marvin_rate_init(&b,direction);
  for(int i=0;i<rate;i++)input[i]=(int16_t)(12000*sin(2*3.141592653589793*1000*i/rate));
  size_t n=marvin_rate_convert(&a,input,rate,whole,36000),used=0;assert(n==(direction?24000:16000));
  for(int i=0;i<rate;){size_t count=(size_t)(i%157+1);if(count>(size_t)(rate-i))count=rate-i;used+=marvin_rate_convert(&b,input+i,count,chunks+used,36000-used);i+=(int)count;}
  assert(n==used&&!memcmp(whole,chunks,n*2));assert(fabs(rms(whole,n)-12000/sqrt(2))<100);
  marvin_rate_t old=a;assert(!marvin_rate_convert(&a,input,160,chunks,1));assert(!memcmp(&a,&old,sizeof(a)));
 }
 marvin_rate_t r;marvin_rate_init(&r,false);for(int i=0;i<24000;i++)input[i]=(int16_t)(12000*sin(2*3.141592653589793*10000*i/24000));size_t n=marvin_rate_convert(&r,input,24000,whole,36000);assert(rms(whole,n)<30);
 puts("Audio conversion: exact 16/24 kHz counts, arbitrary chunk continuity, 1 kHz gain, >49 dB rejection at 10 kHz, and capacity rollback passed.");
}
