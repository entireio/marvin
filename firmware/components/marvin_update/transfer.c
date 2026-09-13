#include "marvin_update_transfer.h"
#include <string.h>
void marvin_update_transfer_init(marvin_update_transfer_t *t,marvin_update_writer_t writer){
    memset(t,0,sizeof(*t));t->writer=writer;mbedtls_sha256_init(&t->hash);
}
void marvin_update_transfer_cancel(marvin_update_transfer_t *t){
    if(!t || t->state==MARVIN_UPDATE_SELECTED)return;
    if(t->opened){t->writer.abort(t->writer.context);t->opened=false;}
    mbedtls_sha256_free(&t->hash);t->state=MARVIN_UPDATE_FAILED;
}
bool marvin_update_transfer_begin(marvin_update_transfer_t *t,const uint8_t *manifest,size_t bytes,const marvin_update_trust_t *trust){
    if(!t||t->state!=MARVIN_UPDATE_EMPTY)return false;
    if(!t->writer.begin||!t->writer.write||!t->writer.finish||!t->writer.select||!t->writer.abort||
       !marvin_update_verify(manifest,bytes,trust,&t->image)||mbedtls_sha256_starts(&t->hash,0)){
        marvin_update_transfer_cancel(t);return false;
    }
    if(!t->writer.begin(t->writer.context,t->image.image_bytes)){marvin_update_transfer_cancel(t);return false;}
    t->opened=true;t->state=MARVIN_UPDATE_RECEIVING;return true;
}
bool marvin_update_transfer_write(marvin_update_transfer_t *t,const uint8_t *data,size_t bytes){
    if(!t||t->state!=MARVIN_UPDATE_RECEIVING)return false;
    if(!data||!bytes||bytes>t->image.image_bytes-t->received||
       !t->writer.write(t->writer.context,data,bytes)||mbedtls_sha256_update(&t->hash,data,bytes)){
        marvin_update_transfer_cancel(t);return false;
    }
    t->received+=(uint32_t)bytes;return true;
}
bool marvin_update_transfer_finish(marvin_update_transfer_t *t){
    if(!t||t->state!=MARVIN_UPDATE_RECEIVING)return false;
    uint8_t digest[32];
    if(t->received!=t->image.image_bytes||mbedtls_sha256_finish(&t->hash,digest)||memcmp(digest,t->image.image_sha256,32)||
       !t->writer.finish(t->writer.context)||!t->writer.select(t->writer.context,&t->image)){
        marvin_update_transfer_cancel(t);return false;
    }
    mbedtls_sha256_free(&t->hash);t->opened=false;t->state=MARVIN_UPDATE_SELECTED;return true;
}
