#include "marvin_update_transfer.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
typedef struct {int fault,aborts,writes;bool selected,finished;size_t bytes;uint8_t slot[4096];} board_t;
static bool begin(void *context,uint32_t bytes){board_t *b=context;assert(bytes==4096);return b->fault!=1;}
static bool write_chunk(void *context,const uint8_t *data,size_t bytes){board_t *b=context;b->writes++;if(b->fault==2&&b->writes==3)return false;assert(b->bytes+bytes<=4096);memcpy(b->slot+b->bytes,data,bytes);b->bytes+=bytes;return true;}
static bool finish(void *context){board_t *b=context;if(b->fault==3)return false;b->finished=true;return true;}
static bool select_image(void *context,const marvin_update_image_t *image){board_t *b=context;assert(b->finished&&image->sequence==4&&b->bytes==4096);if(b->fault==4)return false;b->selected=true;return true;}
static void abort_image(void *context){board_t *b=context;b->aborts++;assert(!b->selected);}
int main(int argc,char **argv){
 assert(argc==2);char path[512],pem[1024]={0};uint8_t manifest[176],chunk[256];memset(chunk,0x42,sizeof(chunk));
 snprintf(path,sizeof(path),"%s/public.pem",argv[1]);FILE *f=fopen(path,"rb");assert(f);assert(fread(pem,1,sizeof(pem)-1,f)>0);fclose(f);
 snprintf(path,sizeof(path),"%s/transfer.bin",argv[1]);f=fopen(path,"rb");assert(f);assert(fread(manifest,1,sizeof(manifest),f)==176);fclose(f);
 marvin_update_trust_t trust={pem,"waveshare-esp32s3-audio","afe-v1",3,0x1e0000};
 for(int repeat=0;repeat<10;repeat++)for(int fault=0;fault<=7;fault++){
  board_t b={.fault=fault};marvin_update_transfer_t t;marvin_update_transfer_init(&t,(marvin_update_writer_t){&b,begin,write_chunk,finish,select_image,abort_image});
  bool started=marvin_update_transfer_begin(&t,manifest,sizeof(manifest),&trust);assert(started==(fault!=1));
  if(started){
   for(int i=0;i<16;i++){
    assert(!b.selected);
    if(fault==5&&i==repeat%16){marvin_update_transfer_cancel(&t);break;}
    if(fault==6&&i==15)break;
    chunk[0]=(fault==7&&i==8)?0x43:0x42;
    if(!marvin_update_transfer_write(&t,chunk,sizeof(chunk)))break;
   }
   bool selected=marvin_update_transfer_finish(&t);assert(selected==(fault==0));
  }
  assert(b.selected==(fault==0));marvin_update_transfer_cancel(&t);marvin_update_transfer_cancel(&t);
  assert(b.aborts==(fault==0||fault==1?0:1));
  assert(!marvin_update_transfer_write(&t,chunk,sizeof(chunk)));assert(!marvin_update_transfer_finish(&t));
 }
 puts("Update transfer: valid selection and seven failure/interrupt modes each passed 10 trials; no failed transfer selected an image.");
 return 0;
}
