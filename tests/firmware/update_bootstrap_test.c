#define main platform_original_main
#include "update_platform_test.c"
#undef main
int main(int argc,char **argv){assert(argc==2);char path[512],pem[1024]={0};uint8_t capsule[176];
 snprintf(path,sizeof(path),"%s/public.pem",argv[1]);FILE *f=fopen(path,"rb");assert(f);assert(fread(pem,1,sizeof(pem)-1,f));fclose(f);
 snprintf(path,sizeof(path),"%s/bootstrap.bin",argv[1]);f=fopen(path,"rb");assert(f);assert(fread(capsule,1,176,f)==176);fclose(f);
 marvin_update_trust_t trust={pem,"waveshare-esp32s3-audio","afe-v1",0,4096};
 for(int trial=0;trial<10;trial++){
 reset();running=1;valid=0;memset(flash,0x42,sizeof(flash));assert(!marvin_update_esp_bootstrap(false,capsule,176,&trust));assert(marvin_update_esp_bootstrap(true,capsule,176,&trust));assert(valid&&floor_seq==1&&!pending_size);assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));
 reset();valid=0;memset(flash,0x42,sizeof(flash));flash[1]^=1;assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));assert(!pending_size&&!valid&&!floor_seq);
 reset();valid=0;memset(flash,0x42,sizeof(flash));capsule[175]^=1;assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));capsule[175]^=1;assert(!pending_size);
 reset();valid=0;memset(flash,0x42,sizeof(flash));commit_fail=1;assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));assert(!valid&&!floor_seq);
 reset();valid=0;memset(flash,0x42,sizeof(flash));pending_size=48;assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));assert(pending_size==48&&!valid);
 reset();valid=0;memset(flash,0x42,sizeof(flash));rollback_available=false;assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust));assert(!pending_size&&!valid);
 reset();memset(flash,0x42,sizeof(flash));assert(!marvin_update_esp_bootstrap(true,capsule,176,&trust)); /* Non-trial image. */
 }
 puts("Signed bootstrap fixture: 70 valid/health/digest/signature/persistence/state/pending/fallback trials passed; bootstrap cannot replay after sequence commit. No physical installation.");
}
