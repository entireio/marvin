#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mbed=work/esp-idf/components/mbedtls/mbedtls
json=work/esp-idf/components/json/cJSON
cmake -S "$mbed" -B work/mbedtls-host -DENABLE_TESTING=OFF -DENABLE_PROGRAMS=OFF > work/board/mbedtls-host-configure.log
cmake --build work/mbedtls-host -j 4 > work/board/mbedtls-host-build.log
node --import tsx scripts/ticket-vectors.ts
# Upstream cJSON triggers Apple's sprintf deprecation warning. Keep our code under -Werror.
cc -std=c11 -fsanitize=address,undefined -Wno-deprecated-declarations -I "$json" -c "$json/cJSON.c" -o work/firmware-ticket-vectors/cjson.o
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/components/marvin_enrollment/include -I "$mbed/include" -I "$json" firmware/components/marvin_enrollment/ticket.c tests/firmware/ticket_test.c work/firmware-ticket-vectors/cjson.o work/mbedtls-host/library/libmbedcrypto.a -o work/firmware-ticket-vectors/ticket-test
UBSAN_OPTIONS=halt_on_error=1 work/firmware-ticket-vectors/ticket-test work/firmware-ticket-vectors/public.pem work/firmware-ticket-vectors/cases.json
