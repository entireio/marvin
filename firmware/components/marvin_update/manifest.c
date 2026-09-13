#include "marvin_update.h"
#include <string.h>
#include "mbedtls/pk.h"
#include "mbedtls/ecdsa.h"
#include "mbedtls/sha256.h"

static uint32_t read32(const uint8_t *p) {
    return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];
}
static bool field(const uint8_t *data, size_t capacity, const char *expected) {
    if (!expected) return false;
    size_t n = strlen(expected);
    if (!n || n >= capacity || memcmp(data, expected, n)) return false;
    for (size_t i=n; i<capacity; i++) if (data[i]) return false;
    return true;
}
bool marvin_update_verify(const uint8_t *m, size_t length,
                          const marvin_update_trust_t *trust,
                          marvin_update_image_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!m || !trust || !out || !trust->public_key_pem || length != MARVIN_UPDATE_MANIFEST_BYTES) return false;
    if (memcmp(m, "MRVOTA01", 8) || !field(m+48,32,trust->board) || !field(m+80,16,trust->layout)) return false;
    uint32_t sequence=read32(m+8), bytes=read32(m+12), minimum=read32(m+96);
    if (!sequence || sequence<=trust->committed_sequence || minimum>trust->committed_sequence || bytes<1024 || bytes>trust->slot_bytes) return false;
    for (size_t i=100; i<112; i++) if (m[i]) return false;
    mbedtls_pk_context key;
    mbedtls_mpi r, s;
    mbedtls_pk_init(&key);
    mbedtls_mpi_init(&r);
    mbedtls_mpi_init(&s);
    bool ok=false;
    uint8_t digest[32];
    if (mbedtls_sha256(m,112,digest,0) ||
        mbedtls_pk_parse_public_key(&key,(const uint8_t*)trust->public_key_pem,strlen(trust->public_key_pem)+1) ||
        !mbedtls_pk_can_do(&key,MBEDTLS_PK_ECDSA) || mbedtls_pk_get_bitlen(&key)!=256) goto done;
    mbedtls_ecp_keypair *ec=mbedtls_pk_ec(key);
    if (ec->MBEDTLS_PRIVATE(grp).id!=MBEDTLS_ECP_DP_SECP256R1 ||
        mbedtls_mpi_read_binary(&r,m+112,32) || mbedtls_mpi_read_binary(&s,m+144,32) ||
        mbedtls_ecdsa_verify(&ec->MBEDTLS_PRIVATE(grp),digest,sizeof(digest),&ec->MBEDTLS_PRIVATE(Q),&r,&s)) goto done;
    out->sequence=sequence;
    out->image_bytes=bytes;
    memcpy(out->image_sha256,m+16,32);
    ok=true;
done:
    mbedtls_mpi_free(&r);
    mbedtls_mpi_free(&s);
    mbedtls_pk_free(&key);
    return ok;
}
