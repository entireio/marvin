#include "marvin_update.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dirent.h>
int main(int argc,char **argv){
 assert(argc==2);char path[512],pem[1024]={0};
 snprintf(path,sizeof(path),"%s/public.pem",argv[1]);FILE *f=fopen(path,"rb");assert(f);size_t key_bytes=fread(pem,1,sizeof(pem)-1,f);fclose(f);assert(key_bytes>0);
 marvin_update_trust_t trust={pem,"waveshare-esp32s3-audio","afe-v1",3,0x1e0000};
 DIR *dir=opendir(argv[1]);assert(dir);struct dirent *entry;unsigned count=0;
 while((entry=readdir(dir))){bool yes=!strncmp(entry->d_name,"yes-",4);if(!yes&&strncmp(entry->d_name,"no-",3))continue;
  snprintf(path,sizeof(path),"%s/%s",argv[1],entry->d_name);f=fopen(path,"rb");assert(f);uint8_t bytes[177];size_t n=fread(bytes,1,sizeof(bytes),f);fclose(f);marvin_update_image_t image;memset(&image,0xff,sizeof(image));
  bool result=marvin_update_verify(bytes,n,&trust,&image);if(result!=yes)fprintf(stderr,"Unexpected result: %s\n",entry->d_name);assert(result==yes);
  if(yes){assert(image.sequence==4&&image.image_bytes==4096);for(unsigned i=0;i<32;i++)assert(image.image_sha256[i]==0x5a);}
  else{marvin_update_image_t zero={0};assert(!memcmp(&zero,&image,sizeof(image)));}
  for(size_t i=0;i<n;i++)assert(!marvin_update_verify(bytes,i,&trust,&image));count++;
 }
 closedir(dir);assert(count==15);
 uint32_t seed=827;uint8_t fuzz[177];marvin_update_image_t image;
 for(unsigned i=0;i<10000;i++){for(size_t j=0;j<sizeof(fuzz);j++){seed=1664525*seed+1013904223;fuzz[j]=seed>>24;}assert(!marvin_update_verify(fuzz,i%178,&trust,&image));}
 assert(!marvin_update_verify(NULL,176,&trust,&image));
 puts("Update manifest: 15 signed/invalid vectors, every truncation and 10,000 malformed envelopes passed.");
 return 0;
}
