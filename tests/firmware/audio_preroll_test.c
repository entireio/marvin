#include "audio_preroll.h"
#include <assert.h>
#include <stdio.h>
int main(void){
 marvin_preroll_t p={0};int16_t in[6000],out[160];for(int i=0;i<6000;i++)in[i]=(int16_t)i;
 marvin_preroll_push(&p,in,6000);assert(p.count==4800);
 for(int part=0;part<30;part++){assert(marvin_preroll_take(&p,out,160)==160);for(int i=0;i<160;i++)assert(out[i]==1200+part*160+i);}
 assert(marvin_preroll_take(&p,out,160)==0);for(int i=0;i<4800;i++)assert(p.pcm[i]==0);
 marvin_preroll_push(&p,in,57);assert(marvin_preroll_take(&p,out,160)==57);
 marvin_preroll_push(&p,in,6000);marvin_preroll_clear(&p);assert(!p.count);for(int i=0;i<4800;i++)assert(p.pcm[i]==0);
 puts("Pre-roll chronological wrap, partial drain, bounded retention and erasure pass");
}
