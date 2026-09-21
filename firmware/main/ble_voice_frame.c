#include "ble_voice_frame.h"
#include <string.h>
#define MAGIC0 'M'
#define MAGIC1 'B'
#define MAGIC2 'L'
#define MAGIC3 '1'
void marvin_ble_frame_reset(marvin_ble_frame_decoder_t *d){memset(d,0,sizeof(*d));}
bool marvin_ble_frame_append(marvin_ble_frame_decoder_t *d,const uint8_t *f,size_t n,int64_t now,marvin_ble_message_t *complete){
 if(!d||!f||!complete||n<=MARVIN_BLE_FRAME_HEADER||n>MARVIN_BLE_ATT_PAYLOAD||f[0]!=MAGIC0||f[1]!=MAGIC1||f[2]!=MAGIC2||f[3]!=MAGIC3||f[11])return false;
 marvin_ble_frame_kind_t kind=(marvin_ble_frame_kind_t)f[4];uint16_t seq=((uint16_t)f[5]<<8)|f[6],size=((uint16_t)f[9]<<8)|f[10];uint8_t part=f[7],total=f[8];size_t body=n-MARVIN_BLE_FRAME_HEADER;
 if((kind!=MARVIN_BLE_CONTROL&&kind!=MARVIN_BLE_AUDIO)||!total||part>=total||!size||size>MARVIN_BLE_FRAME_MAX_MESSAGE)return false;
 if(d->active&&now>d->deadline_us)marvin_ble_frame_reset(d);
 if(!d->active){if(part)return false;d->active=true;d->kind=kind;d->sequence=seq;d->length=size;d->total=total;d->deadline_us=now+2000000;}
 if(d->kind!=kind||d->sequence!=seq||d->length!=size||d->total!=total||d->next_part!=part||d->received+body>size)return false;
 memcpy(d->message.data+d->received,f+MARVIN_BLE_FRAME_HEADER,body);d->received+=body;d->next_part++;
 if(d->next_part!=total)return false;
 if(d->received!=size){marvin_ble_frame_reset(d);return false;}d->message.kind=kind;d->message.sequence=seq;d->message.length=size;*complete=d->message;marvin_ble_frame_reset(d);return true;
}
