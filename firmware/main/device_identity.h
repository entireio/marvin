#pragma once
#include "esp_err.h"
#include "cJSON.h"
esp_err_t marvin_identity_init(void);
/* Call only inside an authenticated Security 2 setup session. */
esp_err_t marvin_identity_challenge(const char *operation,double browser_time,cJSON *reply);

const char *marvin_identity_device_id(void);
esp_err_t marvin_identity_redemption_proof(const char *ticket,const char *network,char proof[100]);
