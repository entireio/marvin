#include "connection_choreography.h"

static uint32_t random_next(uint32_t *state){
    /* xorshift32 is sufficient here: this is animation variation, not entropy. */
    uint32_t value=*state?*state:0x6d2b79f5u;
    value^=value<<13;value^=value>>17;value^=value<<5;
    return *state=value;
}
static int between(uint32_t *state,int low,int high){
    return low+(int)(random_next(state)%(uint32_t)(high-low+1));
}
static void add(marvin_connection_choreography_t *plan,int yaw,int pitch,int turn,
                int gaze_x,int gaze_y,int duration,marvin_connection_eyes_t eyes){
    if(plan->count>=MARVIN_CONNECTION_CHOREOGRAPHY_MAX_STEPS)return;
    plan->steps[plan->count++]=(marvin_connection_step_t){
        .yaw=(int8_t)yaw,.pitch=(int8_t)pitch,.turn_percent=(int8_t)turn,
        .gaze_x=(int16_t)gaze_x,.gaze_y=(int16_t)gaze_y,
        .duration_ms=(uint16_t)duration,.eyes=eyes,
    };
}
void marvin_connection_choreography_plan(marvin_connection_choreography_t *plan,
                                         uint32_t seed){
    if(!plan)return;
    *plan=(marvin_connection_choreography_t){0};
    uint32_t random=seed?seed:0x91e10da5u;
    int direction=(random_next(&random)&1u)?1:-1;
    int first_yaw=direction*between(&random,18,34);
    int first_pitch=between(&random,-13,10);
    int second_yaw=-direction*between(&random,12,30);
    int second_pitch=between(&random,-9,14);
    int turn_slow_a=between(&random,380,560);
    int turn_fast=between(&random,650,880);
    int turn_slow_b=between(&random,360,540);

    /* Anticipation, asymmetry, overshoot and varied holds make the head path
     * read as attention rather than a servo calibration routine. */
    add(plan,direction*between(&random,8,15),-between(&random,4,11),0,
        direction*between(&random,420,700),-between(&random,80,260),between(&random,320,480),MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,first_yaw,first_pitch,0,direction*between(&random,650,920),
        first_pitch*14,between(&random,620,980),MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,first_yaw,first_pitch,0,direction*between(&random,300,560),
        first_pitch*10,between(&random,360,760),MARVIN_CONNECTION_EYES_FOCUSED);
    add(plan,second_yaw,second_pitch,0,-direction*between(&random,560,880),
        second_pitch*13,between(&random,520,900),MARVIN_CONNECTION_EYES_CURIOUS);

    /* Head and eyes lead the turn. Track power follows slow-fast-slow timing;
     * the mirrored return uses identical durations and therefore approximately
     * cancels the rotation despite randomized pacing. */
    add(plan,direction*32,-4,direction*60,direction*900,-80,turn_slow_a,MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,direction*38,2,direction*82,direction*980,20,turn_fast,MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,direction*27,7,direction*60,direction*720,130,turn_slow_b,MARVIN_CONNECTION_EYES_FOCUSED);
    add(plan,direction*18,between(&random,-8,8),0,direction*420,0,between(&random,300,480),MARVIN_CONNECTION_EYES_FOCUSED);

    add(plan,-direction*between(&random,10,26),between(&random,-12,12),0,
        -direction*between(&random,500,850),between(&random,-180,220),between(&random,580,940),MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,-direction*between(&random,10,26),between(&random,-12,12),0,
        -direction*between(&random,280,520),between(&random,-140,180),between(&random,480,900),MARVIN_CONNECTION_EYES_FOCUSED);
    add(plan,direction*between(&random,8,20),between(&random,-8,10),0,
        direction*between(&random,420,720),between(&random,-120,170),between(&random,520,860),MARVIN_CONNECTION_EYES_CURIOUS);

    add(plan,-direction*27,7,-direction*60,-direction*720,130,turn_slow_b,MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,-direction*38,2,-direction*82,-direction*980,20,turn_fast,MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,-direction*32,-4,-direction*60,-direction*900,-80,turn_slow_a,MARVIN_CONNECTION_EYES_FOCUSED);
    add(plan,-direction*10,3,0,-direction*300,40,between(&random,300,480),MARVIN_CONNECTION_EYES_CURIOUS);
    add(plan,0,0,0,0,0,between(&random,650,950),MARVIN_CONNECTION_EYES_NEUTRAL);
}
