#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#define MARVIN_FRAME_MAX 4096
#define MARVIN_DEVICE_PROTOCOL_MINOR 6
typedef struct {size_t length;bool binary;char text[MARVIN_FRAME_MAX+1];} marvin_frame_t;
typedef struct {marvin_frame_t message;size_t received,total;uint8_t opcode;bool active,poisoned,fin,allow_audio;} marvin_wire_t;
typedef enum {MARVIN_WIRE_ERROR=-1,MARVIN_WIRE_MORE=0,MARVIN_WIRE_COMPLETE=1} marvin_wire_result_t;
void marvin_wire_reset(marvin_wire_t *wire);
/* Handles both WebSocket fragmentation and SDK delivery in buffer-sized chunks. */
marvin_wire_result_t marvin_wire_append(marvin_wire_t *wire,uint8_t opcode,bool fin,int offset,int total,const char *data,int length);
/* Presence profile accepts only a matching welcome followed by application pings. */
bool marvin_wire_control(const char *message,uint32_t epoch,bool welcomed);
