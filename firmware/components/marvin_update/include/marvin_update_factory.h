#pragma once
#include "marvin_update.h"
/* layout and slot size must come from the compiled profile/partition table.
 * key_buffer must remain alive through verification and download. On failure
 * both caller-owned outputs are cleared. Factory NVS must already be mounted. */
bool marvin_update_factory_trust(char *key_buffer,size_t capacity,const char *layout,
 uint32_t slot_bytes,marvin_update_trust_t *trust);
/* Public signed capsule only; verification happens in bootstrap/transfer code. */
bool marvin_update_factory_bootstrap(uint8_t manifest[MARVIN_UPDATE_MANIFEST_BYTES]);
