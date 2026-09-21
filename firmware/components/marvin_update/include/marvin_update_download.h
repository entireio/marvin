#pragma once
#include "marvin_update_transfer.h"
/* Caller owns exclusive update admission and must quiesce audio before calling.
 * origin is the enrolled HTTPS origin, token the current device credential, and
 * ca_pem the locally provisioned CA (NULL uses ESP-IDF's public trust bundle).
 * No redirects, query strings, alternate download hosts or bearer logs. */
bool marvin_update_download(const char *origin,const char *token,const char *ca_pem,
 uint32_t sequence,const marvin_update_trust_t *trust,marvin_update_writer_t writer,
 bool (*cancelled)(void *context),void *context);
