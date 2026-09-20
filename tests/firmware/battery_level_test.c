#include "battery_level.h"
#include <assert.h>
#include <stdio.h>

int main(void){
 assert(marvin_battery_percent(2500)==0);
 assert(marvin_battery_percent(3300)==0);
 assert(marvin_battery_percent(3450)==5);
 assert(marvin_battery_percent(3820)==50);
 assert(marvin_battery_percent(4060)==90);
 assert(marvin_battery_percent(4200)==100);
 assert(marvin_battery_percent(4400)==100);
 unsigned previous=0;
 for(unsigned mv=2500;mv<=5000;mv++){unsigned level=marvin_battery_percent(mv);assert(level>=previous&&level<=100);previous=level;}
 puts("Battery level: bounded monotonic single-cell voltage curve passed.");
}
