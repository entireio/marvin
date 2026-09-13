#pragma once
#include <stddef.h>
#include <stdint.h>
#define MARVIN_PREROLL_SAMPLES 4800 /* 300 ms, local RAM only. */
typedef struct {int16_t pcm[MARVIN_PREROLL_SAMPLES];size_t head,count;} marvin_preroll_t;
void marvin_preroll_clear(marvin_preroll_t *p);
void marvin_preroll_push(marvin_preroll_t *p,const int16_t *pcm,size_t count);
size_t marvin_preroll_take(marvin_preroll_t *p,int16_t *pcm,size_t capacity);
