#include "audio_playback_policy.h"
#include <assert.h>

int main(void){
 const int64_t enabled_at=INT64_C(1000000),ready_at=enabled_at+MARVIN_AMPLIFIER_STARTUP_US;
 assert(!marvin_amplifier_ready(ready_at-1,ready_at));
 assert(marvin_amplifier_ready(ready_at,ready_at));

 /* Streaming gaps must not cycle the amplifier before the turn ends. */
 assert(!marvin_playback_should_power_down(false,true,ready_at+1000000,ready_at+10000,ready_at));
 assert(!marvin_playback_should_power_down(true,false,ready_at+1000000,ready_at+10000,ready_at));

 /* A silent turn may close once warm; a spoken turn waits for DMA drain. */
 assert(!marvin_playback_should_power_down(true,true,ready_at-1,0,ready_at));
 assert(marvin_playback_should_power_down(true,true,ready_at,0,ready_at));
 const int64_t last_write=ready_at+INT64_C(20000);
 assert(!marvin_playback_should_power_down(true,true,last_write+MARVIN_PLAYBACK_DRAIN_US-1,last_write,ready_at));
 assert(marvin_playback_should_power_down(true,true,last_write+MARVIN_PLAYBACK_DRAIN_US,last_write,ready_at));
 return 0;
}
