#pragma once
#include <stddef.h>
#include <stdint.h>
#define MARVIN_ADPCM_HEADER_BYTES 14
#define MARVIN_ADPCM_MAX_SAMPLES 960
size_t marvin_adpcm_encode(uint32_t sequence,const int16_t *pcm,size_t samples,uint8_t *output,size_t capacity);
