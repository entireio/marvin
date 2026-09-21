#include "connection_choreography.h"
#include <assert.h>
#include <stdbool.h>
#include <string.h>

static int turn_impulse(const marvin_connection_choreography_t *plan){
    int result=0;
    for(size_t i=0;i<plan->count;i++)result+=plan->steps[i].turn_percent*plan->steps[i].duration_ms;
    return result;
}
int main(void){
    marvin_connection_choreography_t first,repeat,different;
    marvin_connection_choreography_plan(&first,1234);
    marvin_connection_choreography_plan(&repeat,1234);
    marvin_connection_choreography_plan(&different,5678);
    assert(first.count==16);
    assert(!memcmp(&first,&repeat,sizeof(first)));
    assert(memcmp(&first,&different,sizeof(first)));
    assert(turn_impulse(&first)==0);
    bool saw_turn=false,saw_pause_after_turn=false,saw_reverse=false;
    int initial_direction=0;
    for(size_t i=0;i<first.count;i++){
        const marvin_connection_step_t *step=&first.steps[i];
        assert(step->yaw>=-40&&step->yaw<=40);
        assert(step->pitch>=-30&&step->pitch<=30);
        assert(step->turn_percent>=-82&&step->turn_percent<=82);
        assert(step->gaze_x>=-1000&&step->gaze_x<=1000);
        assert(step->gaze_y>=-1000&&step->gaze_y<=1000);
        assert(step->duration_ms>=300&&step->duration_ms<=1000);
        if(step->turn_percent&&!initial_direction)initial_direction=step->turn_percent>0?1:-1;
        if(step->turn_percent){saw_turn=true;if((step->turn_percent>0?1:-1)!=initial_direction)saw_reverse=true;}
        else if(saw_turn&&!saw_reverse)saw_pause_after_turn=true;
    }
    assert(saw_turn&&saw_pause_after_turn&&saw_reverse);
    return 0;
}
