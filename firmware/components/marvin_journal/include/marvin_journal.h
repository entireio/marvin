#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include "marvin_enrollment.h"
#define MARVIN_NETWORK_BYTES 512
#define MARVIN_JOURNAL_VERSION 1
/* Store this entire record with a single atomic NVS blob commit. Never log it. */
typedef struct {
 uint32_t version,epoch,pending_epoch;
 bool linked,pending,network_change,submitted;
 char owner[129],pending_owner[129],credential[96],ticket[4097];
 int64_t credential_expires_ms;
 uint16_t network_size,pending_network_size;
 uint8_t network[MARVIN_NETWORK_BYTES],pending_network[MARVIN_NETWORK_BYTES];
} marvin_journal_record_t;
typedef bool (*marvin_journal_save_t)(void *context,const marvin_journal_record_t *record);
/* Keep off the task stack. Scratch avoids a multi-kilobyte automatic copy. Single caller only. */
typedef struct {marvin_journal_record_t current,scratch;marvin_journal_save_t save;void *context;} marvin_journal_t;
bool marvin_journal_open(marvin_journal_t *journal,const marvin_journal_record_t *saved,marvin_journal_save_t save,void *context);
/* Call only after signature, nonce, issuer, device and expiry verification. Persist before trying Wi-Fi. */
bool marvin_journal_stage(marvin_journal_t *journal,const marvin_ticket_claims_t *claims,const char *ticket,bool network_change,const void *network,size_t network_size);
/* Persist this flag before issuing HTTP; an interrupted request may already have committed. */
bool marvin_journal_submitted(marvin_journal_t *journal);
/* Call only after a verified HTTPS receipt matches the pending device/epoch. A network receipt preserves the credential. */
bool marvin_journal_complete(marvin_journal_t *journal,uint32_t epoch,const char *credential,int64_t expires_ms);
/* Only cancel after confirming the server did not commit, or explicitly revoking that transaction. */
bool marvin_journal_cancel(marvin_journal_t *journal);
void marvin_journal_close(marvin_journal_t *journal);
