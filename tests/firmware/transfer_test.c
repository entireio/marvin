#include "marvin_transfer.h"
#include <assert.h>
#include <string.h>
#include <stdio.h>
int main(void){
 marvin_transfer_t t;uint8_t data[MARVIN_TRANSFER_MAX];for(size_t i=0;i<sizeof(data);i++)data[i]=(uint8_t)i;
 assert(!marvin_transfer_begin(&t,1,4097,0));
 assert(marvin_transfer_begin(&t,1,sizeof(data),100));
 for(size_t at=0;at<sizeof(data);at+=120){size_t n=sizeof(data)-at;if(n>120)n=120;assert(marvin_transfer_append(&t,1,at,data+at,n,200));assert(marvin_transfer_append(&t,1,at,data+at,n,201));}
 size_t length=0;const uint8_t *result=marvin_transfer_finish(&t,1,300,&length);assert(result&&length==sizeof(data)&&!memcmp(result,data,length));
 assert(!marvin_transfer_append(&t,1,4096,data,1,301));assert(!t.active);
 assert(marvin_transfer_begin(&t,1,10,0));assert(!marvin_transfer_append(&t,2,0,data,1,1));assert(!t.active);
 assert(marvin_transfer_begin(&t,1,10,0));assert(!marvin_transfer_append(&t,1,1,data,1,1));
 assert(marvin_transfer_begin(&t,1,10,0));assert(!marvin_transfer_append(&t,1,0,data,1,120000));
 assert(marvin_transfer_begin(&t,1,10,0));assert(!marvin_transfer_finish(&t,1,1,&length));
 assert(marvin_transfer_append(&t,1,0,data,5,1));data[0]^=1;assert(!marvin_transfer_append(&t,1,0,data,5,2));
 marvin_transfer_clear(&t);for(size_t i=0;i<sizeof(t);i++)assert(((uint8_t*)&t)[i]==0);
 puts("Transfer bounds, duplicate integrity, session isolation, expiry and clearing passed");
}
