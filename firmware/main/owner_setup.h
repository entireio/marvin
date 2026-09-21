#pragma once
#include "esp_err.h"
#include "esp_wifi.h"
#include "cJSON.h"
#include "enrollment_client.h"
#include "device_link.h"
/* Caller serializes setup commands; network worker runs only after stage succeeds. */
esp_err_t marvin_owner_setup_init(void);
void marvin_owner_setup_disconnected(void);
bool marvin_owner_setup_control(uint32_t session,const cJSON *request,cJSON *reply);
bool marvin_owner_setup_stage(uint32_t session,const wifi_config_t *candidate);
bool marvin_owner_setup_pending(wifi_config_t *candidate);
bool marvin_owner_setup_network(wifi_config_t *committed);
bool marvin_owner_setup_linked(void);
marvin_redeem_status_t marvin_owner_setup_redeem(const char *ca);
bool marvin_owner_setup_cancel(void);
/* Local-dev USB recovery only: removes the durable owner journal, never the
 * factory identity or factory trust. The caller must reboot after success. */
bool marvin_owner_setup_reset_local_development(void);
bool marvin_owner_setup_submitted(void);
bool marvin_owner_setup_identity(marvin_link_identity_t *identity);
