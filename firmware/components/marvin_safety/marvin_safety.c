#include "marvin_safety.h"
#include <math.h>
#include <string.h>
void marvin_safety_stop(marvin_safety_t *s){s->moving=false;s->left=s->right=0;s->deadline_ms=0;if(s->cut_motors)s->cut_motors(s->context);}
void marvin_safety_init(marvin_safety_t *s,marvin_hardware_t hw,uint32_t epoch,void(*cut)(void*),void *context){memset(s,0,sizeof(*s));s->hardware=hw;s->epoch=epoch;s->cut_motors=cut;s->context=context;marvin_safety_stop(s);}
void marvin_safety_backend(marvin_safety_t *s,bool connected,uint64_t now){s->connected=connected;s->last_backend_ms=now;if(!connected)marvin_safety_stop(s);}
bool marvin_safety_drive(marvin_safety_t *s,uint32_t epoch,float left,float right,uint32_t duration,uint64_t now){
 if(s->latched||!s->hardware.cliff||!s->connected||!s->cut_motors||epoch!=s->epoch||now<s->last_backend_ms||now-s->last_backend_ms>1000||duration<50||duration>1000||!isfinite(left)||!isfinite(right)||fabsf(left)>.3f||fabsf(right)>.3f||UINT64_MAX-now<duration)return false;
 s->left=left;s->right=right;s->deadline_ms=now+duration;s->moving=left!=0||right!=0;return true;
}
void marvin_safety_tick(marvin_safety_t *s,marvin_hazard_t h,uint64_t now){
 /* A reported hazard is always honored, even if discovery said the sensor was absent. */
 if(h.cliff||h.obstacle||h.picked_up||h.estop||h.watchdog){s->latched=true;marvin_safety_stop(s);return;}
 if(s->moving&&(!s->connected||now<s->last_backend_ms||now-s->last_backend_ms>1000||now>=s->deadline_ms))marvin_safety_stop(s);
}
void marvin_safety_acknowledge(marvin_safety_t *s,marvin_hazard_t h){if(!h.cliff&&!h.obstacle&&!h.picked_up&&!h.estop&&!h.watchdog&&!s->moving)s->latched=false;}
