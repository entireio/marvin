#include "marvin_enrollment.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "cJSON.h"
#include "mbedtls/pk.h"
#include "mbedtls/ecdsa.h"
#include "mbedtls/base64.h"
#include "mbedtls/sha256.h"
static bool decode(const char *data,size_t len,unsigned char *out,size_t capacity,size_t *written){
 if(!len||len>4096||len%4==1)return false;
 size_t padded=(len+3)/4*4;char *copy=malloc(padded+1);if(!copy)return false;
 for(size_t i=0;i<len;i++){char c=data[i];if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-'||c=='_')){free(copy);return false;}copy[i]=c=='-'?'+':c=='_'?'/':c;}
 for(size_t i=len;i<padded;i++){copy[i]='=';}
 copy[padded]=0;
 bool ok=mbedtls_base64_decode(out,capacity,written,(unsigned char*)copy,padded)==0;free(copy);return ok;
}
static cJSON *object(const char *data,size_t len){
 unsigned char *decoded=malloc(2049);if(!decoded)return NULL;size_t size=0;cJSON *value=NULL;
 if(decode(data,len,decoded,2048,&size)&&!memchr(decoded,0,size)){
  decoded[size]=0;bool string=false,escape=false,flat=true;unsigned depth=0;
  for(size_t i=0;i<size;i++){unsigned char c=decoded[i];if(string){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='"')string=false;}else if(c=='"')string=true;else if(c=='{'||c=='['){if(++depth>1){flat=false;break;}}else if(c=='}'||c==']'){if(!depth){flat=false;break;}depth--;}}
  if(flat&&!strstr((char*)decoded,"\\u0000"))value=cJSON_ParseWithOpts((char*)decoded,NULL,true);
 }
 free(decoded);if(!cJSON_IsObject(value)){cJSON_Delete(value);return NULL;}
 /* Reject ambiguous duplicate keys rather than depending on parser ordering. */
 for(cJSON *a=value->child;a;a=a->next)for(cJSON *b=a->next;b;b=b->next)if(!strcmp(a->string,b->string)){cJSON_Delete(value);return NULL;}
 return value;
}
static bool equal(cJSON *object,const char *name,const char *expected){cJSON *v=cJSON_GetObjectItemCaseSensitive(object,name);return expected&&cJSON_IsString(v)&&!strcmp(v->valuestring,expected);}
static bool identifier(cJSON *v){if(!cJSON_IsString(v))return false;size_t n=strlen(v->valuestring);if(!n||n>128)return false;for(size_t i=0;i<n;i++){char c=v->valuestring[i];if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'||c=='-'))return false;}return true;}
static bool integer(cJSON *v,double min,double max){return cJSON_IsNumber(v)&&isfinite(v->valuedouble)&&floor(v->valuedouble)==v->valuedouble&&v->valuedouble>=min&&v->valuedouble<=max;}
bool marvin_ticket_verify(const char *ticket,size_t length,const marvin_ticket_expectation_t *e,marvin_ticket_claims_t *out){
 if(out)memset(out,0,sizeof(*out));
 if(!ticket||!e||!out||!e->public_key_pem||!e->issuer||!e->device_id||!e->nonce||!e->operation||length<3||length>4096||memchr(ticket,0,length)||e->challenge_age_ms>=120000||e->now_seconds<1577836800||e->now_seconds>4102444800)return false;
 if(strlen(e->nonce)!=64)return false;
 for(size_t i=0;i<64;i++)if(!((e->nonce[i]>='0'&&e->nonce[i]<='9')||(e->nonce[i]>='a'&&e->nonce[i]<='f')))return false;
 bool claim=!strcmp(e->operation,"claim"),network=!strcmp(e->operation,"network");if((!claim&&!network)||(network&&(!e->owner||!e->epoch)))return false;
 const char *first=memchr(ticket,'.',length);if(!first)return false;const char *second=memchr(first+1,'.',length-(size_t)(first+1-ticket));if(!second||memchr(second+1,'.',length-(size_t)(second+1-ticket)))return false;
 cJSON *header=object(ticket,(size_t)(first-ticket)),*body=object(first+1,(size_t)(second-first-1));bool ok=false;
 mbedtls_pk_context key;mbedtls_pk_init(&key);mbedtls_mpi r,s;mbedtls_mpi_init(&r);mbedtls_mpi_init(&s);
 if(!header||!body||!equal(header,"alg","ES256")||!equal(header,"typ","JWT"))goto done;
 cJSON *sub=cJSON_GetObjectItemCaseSensitive(body,"sub"),*id=cJSON_GetObjectItemCaseSensitive(body,"jti"),*epoch=cJSON_GetObjectItemCaseSensitive(body,"epoch"),*iat=cJSON_GetObjectItemCaseSensitive(body,"iat"),*exp=cJSON_GetObjectItemCaseSensitive(body,"exp");
 if(!equal(body,"iss",e->issuer)||!equal(body,"aud",e->device_id)||!equal(body,"nonce",e->nonce)||!equal(body,"op",e->operation)||!identifier(sub)||!identifier(id)||!integer(epoch,1,UINT32_MAX)||!integer(iat,1577836800,4102444800)||!integer(exp,1577836800,4102444800))goto done;
 if(exp->valuedouble<=e->now_seconds||iat->valuedouble>e->now_seconds+5||exp->valuedouble<=iat->valuedouble||exp->valuedouble-iat->valuedouble>120||(e->owner&&!equal(body,"sub",e->owner))||(e->epoch&&epoch->valuedouble!=e->epoch))goto done;
 unsigned char signature[64],digest[32];size_t siglen=0;
 if(!decode(second+1,length-(size_t)(second+1-ticket),signature,sizeof(signature),&siglen)||siglen!=64||mbedtls_sha256((const unsigned char*)ticket,(size_t)(second-ticket),digest,0))goto done;
 if(mbedtls_pk_parse_public_key(&key,(const unsigned char*)e->public_key_pem,strlen(e->public_key_pem)+1)||!mbedtls_pk_can_do(&key,MBEDTLS_PK_ECDSA)||mbedtls_pk_get_bitlen(&key)!=256)goto done;
 mbedtls_ecp_keypair *ec=mbedtls_pk_ec(key);if(ec->MBEDTLS_PRIVATE(grp).id!=MBEDTLS_ECP_DP_SECP256R1||mbedtls_mpi_read_binary(&r,signature,32)||mbedtls_mpi_read_binary(&s,signature+32,32)||mbedtls_ecdsa_verify(&ec->MBEDTLS_PRIVATE(grp),digest,sizeof(digest),&ec->MBEDTLS_PRIVATE(Q),&r,&s))goto done;
 strcpy(out->owner,sub->valuestring);strcpy(out->id,id->valuestring);out->epoch=(uint32_t)epoch->valuedouble;out->expires_at=(int64_t)exp->valuedouble;ok=true;
 done:mbedtls_mpi_free(&r);mbedtls_mpi_free(&s);mbedtls_pk_free(&key);cJSON_Delete(header);cJSON_Delete(body);return ok;
}
