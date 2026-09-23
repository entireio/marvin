#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* The Waveshare board's NS4150B specifies a typical 120 ms transition from
 * shutdown to enabled. Keep a measured margin and gate speech on elapsed
 * monotonic time rather than assuming queued DMA silence has already played. */
#define MARVIN_AMPLIFIER_STARTUP_US INT64_C(150000)
#define MARVIN_PLAYBACK_DRAIN_US INT64_C(60000)

static inline bool marvin_amplifier_ready(int64_t now_us,int64_t ready_at_us){
 return ready_at_us>0&&now_us>=ready_at_us;
}

static inline bool marvin_playback_should_power_down(bool turn_ended,bool queue_empty,int64_t now_us,int64_t last_write_us,int64_t ready_at_us){
 if(!turn_ended||!queue_empty||ready_at_us<=0)return false;
 int64_t drained_at=last_write_us>0?last_write_us+MARVIN_PLAYBACK_DRAIN_US:ready_at_us;
 if(drained_at<ready_at_us)drained_at=ready_at_us;
 return now_us>=drained_at;
}
