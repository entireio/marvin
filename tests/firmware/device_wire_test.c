#include "device_wire.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
static marvin_wire_t w;
int main(void){
 const char *welcome="{\"type\":\"welcome\",\"protocol\":{\"major\":1,\"minor\":2},\"epoch\":7,\"heartbeatMs\":5000,\"serverTime\":2000000000000}";
 assert(marvin_wire_control(welcome,7,false));assert(!marvin_wire_control(welcome,8,false));assert(!marvin_wire_control(welcome,7,true));
 assert(!marvin_wire_control("{\"type\":\"ping\"}",7,false));assert(marvin_wire_control("{\"type\":\"ping\"}",7,true));
 assert(!marvin_wire_control("{\"type\":\"ping\",\"type\":\"command\"}",7,true));assert(!marvin_wire_control("{\"type\":\"ping\\u0000command\"}",7,true));
 assert(!marvin_wire_control("{\"type\":\"command\",\"action\":\"tracks\"}",7,true));assert(!marvin_wire_control("{\"type\":\"error\",\"code\":\"DEVICE_REVOKED\"}",7,true));assert(!marvin_wire_control("{\"type\":\"ping\"}junk",7,true));
 size_t n=strlen(welcome);
 for(size_t split=1;split<n;split++){
  marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,0,(int)n,welcome,(int)split)==MARVIN_WIRE_MORE);assert(marvin_wire_append(&w,1,true,(int)split,(int)n,welcome+split,(int)(n-split))==MARVIN_WIRE_COMPLETE);assert(!strcmp(w.message.text,welcome));
  marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,false,0,(int)split,welcome,(int)split)==MARVIN_WIRE_MORE);assert(marvin_wire_append(&w,9,true,0,0,NULL,0)==MARVIN_WIRE_MORE);assert(marvin_wire_append(&w,0,true,0,(int)(n-split),welcome+split,(int)(n-split))==MARVIN_WIRE_COMPLETE);assert(!strcmp(w.message.text,welcome));
 }
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,0,2049,"x",1)==MARVIN_WIRE_ERROR);assert(marvin_wire_append(&w,1,true,0,1,"x",1)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,0,true,0,1,"x",1)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,2,true,0,1,"x",1)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);w.allow_audio=true;const char binary[]={0,1,0,2};assert(marvin_wire_append(&w,2,true,0,4,binary,4)==MARVIN_WIRE_COMPLETE);assert(w.message.binary&&w.message.length==4&&!memcmp(w.message.text,binary,4));
 marvin_wire_reset(&w);w.allow_audio=true;assert(marvin_wire_append(&w,2,false,0,2,binary,2)==MARVIN_WIRE_MORE);assert(marvin_wire_append(&w,0,true,0,2,binary+2,2)==MARVIN_WIRE_COMPLETE);assert(w.message.binary&&w.message.length==4&&!memcmp(w.message.text,binary,4));
 marvin_wire_reset(&w);w.allow_audio=true;assert(marvin_wire_append(&w,2,true,0,4097,binary,1)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,0,2,"\0x",2)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,1,2,"x",1)==MARVIN_WIRE_ERROR);
 marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,0,2,"x",1)==MARVIN_WIRE_MORE);assert(marvin_wire_append(&w,1,true,0,1,"x",1)==MARVIN_WIRE_ERROR);
 char maximum[2048];memset(maximum,'a',sizeof(maximum));marvin_wire_reset(&w);assert(marvin_wire_append(&w,1,true,0,2048,maximum,2048)==MARVIN_WIRE_COMPLETE);assert(w.message.length==2048);
 /* Deterministic malformed-frame corpus runs under ASan/UBSan. */
 srand(7);for(int i=0;i<10000;i++){marvin_wire_reset(&w);for(int j=0;j<5;j++){int total=rand()%3000-5,offset=rand()%3000-5,length=rand()%20-5;char bytes[20]={0};marvin_wire_append(&w,(uint8_t)(rand()%16),rand()%2,offset,total,bytes,length);}}
 puts("Device wire: all split boundaries, continuation/ping interleaving, bounds, epoch/handshake fencing, capability refusal and 10,000 malformed-frame trials passed.");
}
