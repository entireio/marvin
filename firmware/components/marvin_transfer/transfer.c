#include "marvin_transfer.h"
#include <string.h>
void marvin_transfer_clear(marvin_transfer_t *t){volatile uint8_t *p=(volatile uint8_t*)t;for(size_t i=0;i<sizeof(*t);i++)p[i]=0;}
bool marvin_transfer_begin(marvin_transfer_t *t,uint32_t session,size_t total,uint64_t now){
 marvin_transfer_clear(t);if(!total||total>MARVIN_TRANSFER_MAX||now>UINT64_MAX-120000)return false;
 t->session=session;t->total=total;t->deadline_ms=now+120000;t->active=true;return true;
}
static bool valid(marvin_transfer_t *t,uint32_t session,uint64_t now){
 if(!t->active||t->session!=session||now>=t->deadline_ms){marvin_transfer_clear(t);return false;}return true;
}
bool marvin_transfer_append(marvin_transfer_t *t,uint32_t session,size_t offset,const uint8_t *bytes,size_t length,uint64_t now){
 if(!valid(t,session,now))return false;
 if(!bytes||!length||length>MARVIN_TRANSFER_CHUNK_MAX||offset>t->used||length>t->total-offset){marvin_transfer_clear(t);return false;}
 if(offset<t->used){if(length>t->used-offset||memcmp(t->bytes+offset,bytes,length)){marvin_transfer_clear(t);return false;}return true;}
 memcpy(t->bytes+offset,bytes,length);t->used+=length;return true;
}
const uint8_t *marvin_transfer_finish(marvin_transfer_t *t,uint32_t session,uint64_t now,size_t *length){
 if(!length||!valid(t,session,now)||t->used!=t->total)return NULL;
 *length=t->used;return t->bytes;
}
