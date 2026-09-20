#include "battery_level.h"
#include <stddef.h>

typedef struct { unsigned millivolts,percent; } battery_point_t;

/* Approximate rested single-cell Li-ion state of charge. Voltage remains the
 * authoritative measurement: motors, audio and charging can move it
 * temporarily, and the exact curve depends on the installed cell. */
static const battery_point_t curve[]={
 {3300,0},{3450,5},{3680,10},{3740,20},{3770,30},{3790,40},
 {3820,50},{3870,60},{3920,70},{3980,80},{4060,90},{4200,100}
};

unsigned marvin_battery_percent(unsigned millivolts){
 if(millivolts<=curve[0].millivolts)return 0;
 for(size_t i=1;i<sizeof(curve)/sizeof(curve[0]);i++){
  if(millivolts<=curve[i].millivolts){
   unsigned dv=curve[i].millivolts-curve[i-1].millivolts;
   unsigned dp=curve[i].percent-curve[i-1].percent;
   return curve[i-1].percent+(millivolts-curve[i-1].millivolts)*dp/dv;
  }
 }
 return 100;
}
