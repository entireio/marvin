#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Shared on-air frame used only by the local BLE voice bridge.  It keeps GATT
 * fragmentation out of voice/control code and rejects ambiguity on loss. */
#define MARVIN_BLE_ATT_PAYLOAD 240u
#define MARVIN_BLE_FRAME_HEADER 12u
#define MARVIN_BLE_FRAME_PAYLOAD (MARVIN_BLE_ATT_PAYLOAD-MARVIN_BLE_FRAME_HEADER)
#define MARVIN_BLE_FRAME_MAX_MESSAGE 4096u
typedef enum { MARVIN_BLE_CONTROL=1, MARVIN_BLE_AUDIO=2 } marvin_ble_frame_kind_t;
typedef struct { marvin_ble_frame_kind_t kind;uint16_t sequence;uint16_t length;uint8_t data[MARVIN_BLE_FRAME_MAX_MESSAGE]; } marvin_ble_message_t;
typedef struct { bool active;marvin_ble_frame_kind_t kind;uint16_t sequence,length,received;uint8_t total,next_part;int64_t deadline_us;marvin_ble_message_t message; } marvin_ble_frame_decoder_t;
void marvin_ble_frame_reset(marvin_ble_frame_decoder_t *decoder);
bool marvin_ble_frame_append(marvin_ble_frame_decoder_t *decoder,const uint8_t *frame,size_t length,int64_t now_us,marvin_ble_message_t *complete);
