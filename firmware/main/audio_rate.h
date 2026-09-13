#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
/* Streaming rational conversion via a 63-tap low-pass at the shared 48 kHz rate.
 * State survives arbitrary chunk boundaries; no frame-local interpolation seams. */
typedef struct {float coefficients[63],history[63];unsigned position,phase,up,down;} marvin_rate_t;
void marvin_rate_init(marvin_rate_t *rate,bool input_16k);
size_t marvin_rate_convert(marvin_rate_t *rate,const int16_t *input,size_t count,int16_t *output,size_t capacity);
