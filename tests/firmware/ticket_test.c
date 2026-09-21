#include "marvin_enrollment.h"
#include "cJSON.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
static char *file(const char *path){FILE *f=fopen(path,"rb");assert(f);fseek(f,0,SEEK_END);long n=ftell(f);assert(n>=0&&n<1048576);rewind(f);char *p=calloc((size_t)n+1,1);assert(p&&fread(p,1,(size_t)n,f)==(size_t)n);fclose(f);return p;}
static const char *text(cJSON *o,const char *key){cJSON *v=cJSON_GetObjectItemCaseSensitive(o,key);return cJSON_IsString(v)?v->valuestring:NULL;}
static double number(cJSON *o,const char *key){cJSON *v=cJSON_GetObjectItemCaseSensitive(o,key);assert(cJSON_IsNumber(v));return v->valuedouble;}
int main(int argc,char **argv){assert(argc==3);char *pem=file(argv[1]),*raw=file(argv[2]);cJSON *cases=cJSON_Parse(raw);assert(cJSON_IsArray(cases));int passed=0;
 cJSON *item; cJSON_ArrayForEach(item,cases){cJSON *e=cJSON_GetObjectItemCaseSensitive(item,"expect");marvin_ticket_expectation_t expected={.public_key_pem=pem,.issuer=text(e,"issuer"),.device_id=text(e,"device"),.nonce=text(e,"nonce"),.operation=text(e,"operation"),.owner=text(e,"owner"),.epoch=(uint32_t)number(e,"epoch"),.now_seconds=(int64_t)number(e,"now"),.challenge_age_ms=(uint32_t)number(e,"ageMs")};
  const char *token=text(item,"ticket");assert(token);marvin_ticket_claims_t claims;memset(&claims,0xa5,sizeof(claims));bool result=marvin_ticket_verify(token,strlen(token),&expected,&claims),want=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(item,"accept"));if(result!=want){fprintf(stderr,"FAILED: %s\n",text(item,"name"));return 1;}
  if(!result){for(size_t n=0;n<sizeof(claims);n++)assert(((unsigned char*)&claims)[n]==0);}else assert(claims.epoch==7&&claims.owner[0]&&claims.id[0]);passed++;
 }
 printf("%d firmware ticket verification vectors passed\n",passed);cJSON_Delete(cases);free(raw);free(pem);return 0;
}
