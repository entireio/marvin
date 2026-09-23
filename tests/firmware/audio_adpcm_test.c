#include "audio_adpcm.h"
#include <assert.h>
#include <stdint.h>
#include <string.h>
int main(void){
 int16_t silence[480]={0};uint8_t encoded[MARVIN_ADPCM_HEADER_BYTES+MARVIN_ADPCM_MAX_SAMPLES/2];
 size_t length=marvin_adpcm_encode(42,silence,480,encoded,sizeof(encoded));assert(length==254);assert(!memcmp(encoded,"MVA2",4));assert(encoded[4]==0&&encoded[5]==0&&encoded[6]==0&&encoded[7]==42);assert(encoded[8]==1&&encoded[9]==224);for(size_t i=10;i<length;i++)assert(encoded[i]==0);
 int16_t ramp[480];for(size_t i=0;i<480;i++)ramp[i]=(int16_t)(i*50-12000);length=marvin_adpcm_encode(7,ramp,480,encoded,sizeof(encoded));assert(length==254);assert(encoded[10]==(uint8_t)ramp[0]&&encoded[11]==(uint8_t)(ramp[0]>>8));assert(memcmp(encoded+14,encoded+15,100));
 assert(!marvin_adpcm_encode(0,ramp,0,encoded,sizeof(encoded)));assert(!marvin_adpcm_encode(0,ramp,480,encoded,100));return 0;
}
