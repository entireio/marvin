#pragma once
#include "marvin_update.h"
#include "mbedtls/sha256.h"
/* Platform callbacks may only write an inactive slot. finish validates the ESP
 * image; select is the sole operation allowed to change the next boot slot.
 * abort must be idempotent and must never erase the running image. */
typedef struct {
    void *context;
    bool (*begin)(void *context,uint32_t bytes);
    bool (*write)(void *context,const uint8_t *data,size_t bytes);
    bool (*finish)(void *context);
    bool (*select)(void *context,const marvin_update_image_t *image);
    void (*abort)(void *context);
} marvin_update_writer_t;
typedef enum {MARVIN_UPDATE_EMPTY,MARVIN_UPDATE_RECEIVING,MARVIN_UPDATE_FAILED,MARVIN_UPDATE_SELECTED} marvin_update_state_t;
typedef struct {
    marvin_update_writer_t writer;
    marvin_update_image_t image;
    mbedtls_sha256_context hash;
    uint32_t received;
    marvin_update_state_t state;
    bool opened;
} marvin_update_transfer_t;
void marvin_update_transfer_init(marvin_update_transfer_t *transfer,marvin_update_writer_t writer);
bool marvin_update_transfer_begin(marvin_update_transfer_t *transfer,const uint8_t *manifest,size_t bytes,const marvin_update_trust_t *trust);
bool marvin_update_transfer_write(marvin_update_transfer_t *transfer,const uint8_t *data,size_t bytes);
bool marvin_update_transfer_finish(marvin_update_transfer_t *transfer);
void marvin_update_transfer_cancel(marvin_update_transfer_t *transfer);
