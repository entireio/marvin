#include "audio_preroll.h"
#include <string.h>
void marvin_preroll_clear(marvin_preroll_t *p){memset(p,0,sizeof(*p));}
void marvin_preroll_push(marvin_preroll_t *p,const int16_t *pcm,size_t count){
 for(size_t i=0;i<count;i++){if(p->count==MARVIN_PREROLL_SAMPLES){p->head=(p->head+1)%MARVIN_PREROLL_SAMPLES;p->count--;}p->pcm[(p->head+p->count)%MARVIN_PREROLL_SAMPLES]=pcm[i];p->count++;}
}
size_t marvin_preroll_take(marvin_preroll_t *p,int16_t *pcm,size_t capacity){
 size_t n=p->count<capacity?p->count:capacity;
 for(size_t i=0;i<n;i++){pcm[i]=p->pcm[p->head];p->pcm[p->head]=0;p->head=(p->head+1)%MARVIN_PREROLL_SAMPLES;}
 p->count-=n;return n;
}
