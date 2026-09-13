#pragma once
#include <stdint.h>
#include <stdbool.h>
/* No response body or credentials may be logged. RETRY retains the durable pending ticket. */
typedef enum {MARVIN_REDEEM_OK,MARVIN_REDEEM_RETRY,MARVIN_REDEEM_REJECTED,MARVIN_REDEEM_INVALID} marvin_redeem_status_t;
typedef struct {char credential[96];int64_t expires_ms;} marvin_redeem_receipt_t;
marvin_redeem_status_t marvin_enrollment_redeem(const char *origin,const char *ca,const char *ticket,const char *network,uint32_t epoch,bool claim,marvin_redeem_receipt_t *receipt);
