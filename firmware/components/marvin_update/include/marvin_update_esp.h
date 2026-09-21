#pragma once
#include "marvin_update_transfer.h"
#include "esp_ota_ops.h"
/* One installer at a time. The caller must stop audio/radio work that cannot
 * tolerate cache-disabled flash operations before obtaining this writer. */
typedef struct {
 const esp_partition_t *partition;
 esp_ota_handle_t handle;
 bool opened,validated;
} marvin_update_esp_t;
bool marvin_update_esp_writer(marvin_update_esp_t *context,marvin_update_writer_t *writer);
/* Call only after bounded hardware/runtime health checks pass. Recovery after a
 * power cut between boot confirmation and sequence commit is idempotent. */
bool marvin_update_esp_confirm(bool healthy);
bool marvin_update_esp_sequence(uint32_t *sequence);
/* First installation only: persist a verified public factory bootstrap capsule
 * for the currently running trial image, then apply normal health confirmation.
 * Refuses an existing pending update or any nonzero committed sequence. */
bool marvin_update_esp_bootstrap(bool healthy,const uint8_t *manifest,size_t bytes,const marvin_update_trust_t *trust);
