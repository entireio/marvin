#include "marvin_update_factory.h"
#include "nvs.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
static char pem[1024];static size_t length;static int mode;
int nvs_open_from_partition(const char *partition,const char *name,int access,nvs_handle_t *h){assert(!strcmp(partition,"factory")&&!strcmp(name,"identity")&&access==NVS_READONLY);*h=1;return mode==1?1:0;}
int nvs_get_blob(nvs_handle_t h,const char *name,void *out,size_t *size){(void)h;assert(!strcmp(name,"release_pub"));if(mode==2)return 1;if(*size<length)return 1;memcpy(out,pem,length);*size=length;if(mode==3)((char*)out)[length-1]='x';if(mode==4)((char*)out)[10]=0;if(mode==5)((char*)out)[0]='x';return 0;}
void nvs_close(nvs_handle_t h){(void)h;}
bool marvin_update_esp_sequence(uint32_t *sequence){*sequence=3;return mode!=6;}
int main(int argc,char **argv){assert(argc==2);char path[512];snprintf(path,sizeof(path),"%s/public.pem",argv[1]);FILE *f=fopen(path,"rb");assert(f);length=fread(pem,1,sizeof(pem)-1,f)+1;fclose(f);
 for(mode=0;mode<7;mode++){char buffer[1024];memset(buffer,42,sizeof(buffer));marvin_update_trust_t trust;memset(&trust,42,sizeof(trust));bool ok=marvin_update_factory_trust(buffer,sizeof(buffer),"afe-v1",0x1e0000,&trust);assert(ok==(mode==0));if(ok){assert(trust.public_key_pem==buffer&&trust.committed_sequence==3&&!strcmp(buffer,pem));}else{for(size_t i=0;i<sizeof(buffer);i++)assert(buffer[i]==0);assert(!trust.public_key_pem&&!trust.slot_bytes);}}
 char buffer[1024];marvin_update_trust_t trust;mode=0;assert(!marvin_update_factory_trust(buffer,sizeof(buffer),"unknown",0x1e0000,&trust));assert(!marvin_update_factory_trust(buffer,sizeof(buffer),"afe-v1",0x1e0001,&trust));assert(!marvin_update_factory_trust(buffer,10,"afe-v1",0x1e0000,&trust));
 puts("Factory trust loader: valid key/sequence, missing or malformed factory data, failed sequence read, buffer and layout bounds passed.");
}
