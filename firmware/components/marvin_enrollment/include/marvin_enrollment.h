#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
/* Device/backend trust and the challenge are supplied from trusted local state, never from the ticket itself. */
typedef struct {
 const char *public_key_pem,*issuer,*device_id,*nonce,*operation,*owner;
 uint32_t epoch; /* zero only during an initial claim */
 int64_t now_seconds;
 uint32_t challenge_age_ms;
} marvin_ticket_expectation_t;
typedef struct {char owner[129],id[129];uint32_t epoch;int64_t expires_at;} marvin_ticket_claims_t;
bool marvin_ticket_verify(const char *ticket,size_t length,const marvin_ticket_expectation_t *expected,marvin_ticket_claims_t *claims);
