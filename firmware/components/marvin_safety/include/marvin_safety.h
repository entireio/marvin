#pragma once
#include <stdbool.h>
#include <stdint.h>
/* Called by a fixed 10 ms local task; timestamps are monotonic milliseconds. */
typedef struct { bool cliff,tof,imu,second_mic; } marvin_hardware_t;
typedef struct { bool cliff,obstacle,picked_up,estop,watchdog; } marvin_hazard_t;
typedef struct {
    marvin_hardware_t hardware;
    bool connected,moving,latched;
    uint64_t last_backend_ms,deadline_ms;
    float left,right;
    uint32_t epoch;
    void (*cut_motors)(void *context);
    void *context;
} marvin_safety_t;
void marvin_safety_init(marvin_safety_t *s,marvin_hardware_t hardware,uint32_t epoch,void (*cut)(void*),void *context);
void marvin_safety_backend(marvin_safety_t *s,bool connected,uint64_t now);
bool marvin_safety_drive(marvin_safety_t *s,uint32_t epoch,float left,float right,uint32_t duration_ms,uint64_t now);
void marvin_safety_tick(marvin_safety_t *s,marvin_hazard_t hazard,uint64_t now);
void marvin_safety_stop(marvin_safety_t *s);
/* Only a physical acknowledgement can reset an emergency latch. */
void marvin_safety_acknowledge(marvin_safety_t *s,marvin_hazard_t current);
