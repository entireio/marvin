#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
/* Fixed, canonical binary envelope: 112 signed bytes + 64-byte ES256 signature.
 * The trusted key, board, layout and committed sequence come from local state. */
#define MARVIN_UPDATE_MANIFEST_BYTES 176
typedef struct {
    const char *public_key_pem;
    const char *board;
    const char *layout;
    uint32_t committed_sequence;
    uint32_t slot_bytes;
} marvin_update_trust_t;
typedef struct {
    uint32_t sequence;
    uint32_t image_bytes;
    uint8_t image_sha256[32];
} marvin_update_image_t;
bool marvin_update_verify(const uint8_t *manifest, size_t length,
                          const marvin_update_trust_t *trust,
                          marvin_update_image_t *image);
