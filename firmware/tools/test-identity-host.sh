#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mbed=work/esp-idf/components/mbedtls/mbedtls
json=work/esp-idf/components/json/cJSON
cc -std=c11 -Wall -Wextra -Werror -Wno-sign-compare -fsanitize=address,undefined -I tests/firmware/host-identity -I firmware/main -I "$mbed/include" -I "$json" firmware/main/device_identity.c tests/firmware/identity_test.c work/firmware-ticket-vectors/cjson.o work/mbedtls-host/library/libmbedcrypto.a -o work/board/identity-test
UBSAN_OPTIONS=halt_on_error=1 node --import tsx scripts/identity-vectors.ts
