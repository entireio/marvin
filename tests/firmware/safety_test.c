#include "marvin_safety.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>
static void cut(void *ctx){++*(int*)ctx;}
int main(void){
 for(int mask=0;mask<16;mask++)for(int trial=0;trial<100;trial++){
  marvin_safety_t s;int stops=0;marvin_hardware_t hw={(mask&1)!=0,(mask&2)!=0,(mask&4)!=0,(mask&8)!=0};
  marvin_safety_init(&s,hw,7,cut,&stops);assert(stops==1&&!s.moving);marvin_safety_backend(&s,true,100);
  assert(!marvin_safety_drive(&s,6,.1f,.1f,100,100));assert(!marvin_safety_drive(&s,7,NAN,.1f,100,100));assert(!marvin_safety_drive(&s,7,.4f,.1f,100,100));
  bool accepted=marvin_safety_drive(&s,7,.1f,.1f,100,100);assert(accepted==hw.cliff);
  marvin_safety_tick(&s,(marvin_hazard_t){0},200);assert(!s.moving);
  for(int hazard=0;hazard<5;hazard++){
   marvin_hazard_t h={0};if(hazard==0)h.cliff=true;if(hazard==1)h.obstacle=true;if(hazard==2)h.picked_up=true;if(hazard==3)h.estop=true;if(hazard==4)h.watchdog=true;
   marvin_safety_acknowledge(&s,(marvin_hazard_t){0});marvin_safety_backend(&s,true,300);marvin_safety_drive(&s,7,.1f,.1f,100,300);int before=stops;marvin_safety_tick(&s,h,310);assert(stops==before+1&&!s.moving&&s.latched);assert(!marvin_safety_drive(&s,7,.1f,.1f,100,310));marvin_safety_acknowledge(&s,h);assert(s.latched);
  }
  marvin_safety_acknowledge(&s,(marvin_hazard_t){0});marvin_safety_backend(&s,true,400);marvin_safety_drive(&s,7,.1f,.1f,1000,400);marvin_safety_backend(&s,false,401);assert(!s.moving);
  marvin_safety_backend(&s,true,500);assert(!marvin_safety_drive(&s,7,.1f,.1f,100,1501));
 }
 puts("PASS: 16 optional-hardware combinations x 100 trials; five hazards, deadline, disconnect, invalid bounds and epoch. Host logic only; physical latency unmeasured.");
}
