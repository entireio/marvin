#include "eyes_render.h"
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static void pixel(uint8_t *buffer,int x,int y,bool on){
    if(x<0||x>=MARVIN_EYE_WIDTH||y<0||y>=MARVIN_EYE_HEIGHT)return;
    uint8_t mask=(uint8_t)(1U<<(y&7));
    uint8_t *value=&buffer[(y>>3)*MARVIN_EYE_WIDTH+x];
    if(on)*value|=mask;else *value&=(uint8_t)~mask;
}

static void ellipse(uint8_t *buffer,int center_x,int center_y,int rx,int ry,bool on){
    if(rx<=0||ry<=0)return;
    int64_t rx2=(int64_t)rx*rx,ry2=(int64_t)ry*ry,limit=rx2*ry2;
    for(int y=-ry;y<=ry;y++)for(int x=-rx;x<=rx;x++){
        if((int64_t)x*x*ry2+(int64_t)y*y*rx2<=limit)pixel(buffer,center_x+x,center_y+y,on);
    }
}

static void disc(uint8_t *buffer,int center_x,int center_y,int radius){
    ellipse(buffer,center_x,center_y,radius,radius,true);
}

static void happy_arc(uint8_t *buffer,int center_x,int baseline_y,int width,int rise,int thickness){
    int half=width/2;
    if(half<=0)return;
    for(int x=-half;x<=half;x++){
        int y=baseline_y-rise+rise*x*x/(half*half);
        disc(buffer,center_x+x,y,thickness/2);
    }
}

static void horizontal(uint8_t *buffer,int x0,int x1,int y){
    for(int x=x0;x<=x1;x++)pixel(buffer,x,y,true);
}
static void vertical(uint8_t *buffer,int x,int y0,int y1){
    for(int y=y0;y<=y1;y++)pixel(buffer,x,y,true);
}

void marvin_eye_squeeze(marvin_eye_pose_t *pose,int amount){
    if(amount<0)amount=0;else if(amount>1000)amount=1000;
    if(!amount)return;
    int outer_ry=pose->outer_ry;
    int inner_ry=pose->inner_ry;
    /* Compress both layers without ever closing to a line. Their horizontal
     * expansion sells the shape as a soft body being briefly squeezed. */
    pose->outer_ry=outer_ry*(1000-amount*560/1000)/1000;
    if(pose->outer_ry<8)pose->outer_ry=8;
    if(pose->outer_ry>outer_ry)pose->outer_ry=outer_ry;
    pose->outer_rx+=amount*8/1000;
    if(inner_ry>0){
        pose->inner_ry=inner_ry*(1000-amount*600/1000)/1000;
        if(pose->inner_ry<3)pose->inner_ry=3;
        if(pose->inner_ry>inner_ry)pose->inner_ry=inner_ry;
        pose->inner_rx+=amount*4/1000;
    }
}

void marvin_eye_render(uint8_t buffer[MARVIN_EYE_BYTES],const marvin_eye_pose_t *pose,marvin_eye_render_design_t design){
    memset(buffer,0,MARVIN_EYE_BYTES);
    if(design==MARVIN_EYE_RENDER_FRIENDLY&&pose->outer_ry<=14){
        happy_arc(buffer,pose->center_x,pose->center_y+7,pose->outer_rx*2,12,5);
        return;
    }
    ellipse(buffer,pose->center_x,pose->center_y,pose->outer_rx,pose->outer_ry,true);
    if(design==MARVIN_EYE_RENDER_CLASSIC)ellipse(buffer,pose->center_x,pose->center_y,pose->inner_rx,pose->inner_ry,false);
}


void marvin_eye_render_volume(uint8_t buffer[MARVIN_EYE_BYTES],unsigned volume){
    if(volume>100)volume=100;

    /* A four-pixel speaker and a slim, rounded level rail. The whole mark is
     * only 12 px wide, leaving the eye's expression as the dominant image. */
    vertical(buffer,125,29,34);
    vertical(buffer,124,29,34);
    vertical(buffer,123,28,35);
    vertical(buffer,122,27,36);
    pixel(buffer,121,28,true);pixel(buffer,121,35,true);

    horizontal(buffer,116,118,17);horizontal(buffer,116,118,47);
    vertical(buffer,119,18,46);vertical(buffer,115,18,46);
    unsigned filled=(volume*27U+99U)/100U;
    for(unsigned y=0;y<filled;y++)horizontal(buffer,116,117,45-(int)y);
}

void marvin_eye_render_low_battery(uint8_t buffer[MARVIN_EYE_BYTES]){
    /* Horizontal battery silhouette with a deliberately small remaining
     * charge block. It sits flush to the left edge without becoming a second
     * focal point beside the eye. */
    horizontal(buffer,3,13,0);horizontal(buffer,3,13,9);
    vertical(buffer,14,1,8);vertical(buffer,2,1,8);
    vertical(buffer,1,3,6);vertical(buffer,0,3,6);
    for(int y=3;y<=6;y++)horizontal(buffer,11,12,y);
}
