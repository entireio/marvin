#include "eyes_render.h"
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static bool lit(const uint8_t *buffer,int x,int y){
 return (buffer[(y>>3)*MARVIN_EYE_WIDTH+x]&(uint8_t)(1U<<(y&7)))!=0;
}

int main(void){
 uint8_t pixels[MARVIN_EYE_BYTES];
 marvin_eye_pose_t empty={0};

 marvin_eye_pose_t eye={.center_x=64,.center_y=32,.outer_rx=32,.outer_ry=32,.inner_rx=12,.inner_ry=12};
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_CLASSIC);
 assert(lit(pixels,64,0));             /* awake eye uses the display height */
 assert(lit(pixels,64,63));
 assert(!lit(pixels,64,32));           /* centered black core */

 eye.center_x=72;eye.center_y=35;
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_CLASSIC);
 assert(!lit(pixels,32,35));           /* white body moved from its old edge */
 assert(lit(pixels,40,35));            /* new left edge of the complete eye */
 assert(lit(pixels,53,35));            /* core moved too; it did not lag behind */
 assert(!lit(pixels,72,35));

 eye=(marvin_eye_pose_t){.center_x=64,.center_y=32,.outer_rx=32,.outer_ry=32,.inner_rx=12,.inner_ry=12};
 marvin_eye_squeeze(&eye,1000);
 assert(eye.outer_rx==40&&eye.outer_ry==14);
 assert(eye.inner_rx==16&&eye.inner_ry==4);
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_CLASSIC);
 assert(lit(pixels,64,18));            /* compressed body remains substantial */
 assert(!lit(pixels,64,32));           /* compressed core remains visible */

 marvin_eye_render(pixels,&empty,MARVIN_EYE_RENDER_CLASSIC);
 marvin_eye_render_volume(pixels,0);
 assert(lit(pixels,125,31));           /* speaker */
 assert(lit(pixels,119,25));           /* empty level rail */
 assert(!lit(pixels,116,45));          /* no level fill at mute */

 marvin_eye_render(pixels,&empty,MARVIN_EYE_RENDER_CLASSIC);
 marvin_eye_render_volume(pixels,100);
 assert(lit(pixels,116,45));
 assert(lit(pixels,117,19));           /* full rail, inside rounded ends */

 marvin_eye_render(pixels,&empty,MARVIN_EYE_RENDER_CLASSIC);
 marvin_eye_render_volume(pixels,10);
 assert(lit(pixels,116,45));            /* level starts at the screen bottom */
 assert(!lit(pixels,116,42));           /* and grows upward as volume rises */

 marvin_eye_render(pixels,&empty,MARVIN_EYE_RENDER_CLASSIC);
 marvin_eye_render_low_battery(pixels);
 assert(lit(pixels,14,4));             /* battery body at the top edge */
 assert(lit(pixels,0,4));              /* positive terminal */
 assert(lit(pixels,12,4));             /* deliberately low charge */
 assert(!lit(pixels,7,4));

 eye=(marvin_eye_pose_t){.center_x=64,.center_y=32,.outer_rx=32,.outer_ry=32,.inner_rx=12,.inner_ry=12};
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_SOLID);
 assert(lit(pixels,64,32));            /* pupil-free design stays white */

 eye=(marvin_eye_pose_t){.center_x=64,.center_y=32,.outer_rx=25,.outer_ry=31};
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_FRIENDLY);
 assert(lit(pixels,64,1));             /* slightly vertically elongated */
 eye.outer_ry=8;
 marvin_eye_render(pixels,&eye,MARVIN_EYE_RENDER_FRIENDLY);
 assert(!lit(pixels,64,32));           /* blink becomes a smiling arc */
 assert(lit(pixels,64,27));
 return 0;
}
