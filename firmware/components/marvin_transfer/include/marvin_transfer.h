#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#define MARVIN_TRANSFER_MAX 4096
#define MARVIN_TRANSFER_CHUNK_MAX 120
/* Owned by one authenticated BLE session. Erase on disconnect or setup expiry. */
typedef struct {uint8_t bytes[MARVIN_TRANSFER_MAX];uint32_t session;size_t total,used;uint64_t deadline_ms;bool active;} marvin_transfer_t;
void marvin_transfer_clear(marvin_transfer_t *transfer);
bool marvin_transfer_begin(marvin_transfer_t *transfer,uint32_t session,size_t total,uint64_t now_ms);
bool marvin_transfer_append(marvin_transfer_t *transfer,uint32_t session,size_t offset,const uint8_t *bytes,size_t length,uint64_t now_ms);
const uint8_t *marvin_transfer_finish(marvin_transfer_t *transfer,uint32_t session,uint64_t now_ms,size_t *length);
