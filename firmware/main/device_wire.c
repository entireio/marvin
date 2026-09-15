#include "device_wire.h"
#include "cJSON.h"
#include <string.h>
#include <math.h>
void marvin_wire_reset(marvin_wire_t *w){memset(w,0,sizeof(*w));}
static marvin_wire_result_t invalid(marvin_wire_t *w){w->poisoned=true;return MARVIN_WIRE_ERROR;}
marvin_wire_result_t marvin_wire_append(marvin_wire_t *w,uint8_t opcode,bool fin,int offset,int total,const char *data,int length){
 if(w->poisoned)return MARVIN_WIRE_ERROR;
 if(opcode==9||opcode==10)return MARVIN_WIRE_MORE;
 if(length<0||total<0||offset<0||offset>total||length>total-offset||(length&&!data))return invalid(w);
 if(offset==0){
  if(w->active&&w->received!=w->total)return invalid(w);
  if(((opcode==1||opcode==2)&&w->active)||(opcode==0&&!w->active)||(opcode!=0&&opcode!=1&&!(opcode==2&&w->allow_audio)))return invalid(w);
  if(opcode==1||opcode==2){w->message.length=0;w->message.binary=opcode==2;w->active=true;}
  /* Reject advertised oversize before buffering any part of a frame. */
  if((size_t)total>(w->message.binary?MARVIN_FRAME_MAX:2048)-w->message.length)return invalid(w);
  w->total=(size_t)total;w->received=0;w->opcode=opcode;w->fin=fin;
 }
 if(!w->active||opcode!=w->opcode||fin!=w->fin||(size_t)offset!=w->received||(size_t)total!=w->total||w->message.length+(size_t)length>MARVIN_FRAME_MAX)return invalid(w);
 if(length)memcpy(w->message.text+w->message.length,data,(size_t)length);
 w->message.length+=(size_t)length;w->received+=(size_t)length;
 if(w->received==w->total&&fin){
  w->message.text[w->message.length]=0;
  if(!w->message.binary&&memchr(w->message.text,0,w->message.length))return invalid(w);
  w->active=false;return MARVIN_WIRE_COMPLETE;
 }
 return MARVIN_WIRE_MORE;
}
static bool number(const cJSON *v,double expected){return cJSON_IsNumber(v)&&isfinite(v->valuedouble)&&v->valuedouble==expected;}
static bool unique(const cJSON *object){
 if(!cJSON_IsObject(object))return false;
 for(const cJSON *a=object->child;a;a=a->next){
  if(!a->string)return false;
  for(const cJSON *b=a->next;b;b=b->next)if(b->string&&!strcmp(a->string,b->string))return false;
 }
 return true;
}
bool marvin_wire_control(const char *text,uint32_t epoch,bool welcomed){
 if(!text||strstr(text,"\\u0000"))return false;
 cJSON *m=cJSON_ParseWithOpts(text,NULL,true);const cJSON *type=cJSON_GetObjectItemCaseSensitive(m,"type");bool ok=false;
 if(unique(m)&&cJSON_IsString(type)&&!strcmp(type->valuestring,"welcome")&&!welcomed){
  const cJSON *p=cJSON_GetObjectItemCaseSensitive(m,"protocol");
  ok=unique(p)&&number(cJSON_GetObjectItemCaseSensitive(p,"major"),1)&&number(cJSON_GetObjectItemCaseSensitive(p,"minor"),3)&&number(cJSON_GetObjectItemCaseSensitive(m,"epoch"),epoch)&&number(cJSON_GetObjectItemCaseSensitive(m,"heartbeatMs"),5000);
 }else if(unique(m)&&cJSON_IsString(type)&&!strcmp(type->valuestring,"ping")&&welcomed)ok=true;
 cJSON_Delete(m);return ok;
}
