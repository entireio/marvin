#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mbed=work/esp-idf/components/mbedtls/mbedtls
json=work/esp-idf/components/json/cJSON
common="-std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined"
cc $common -I firmware/components/marvin_journal/include -I firmware/components/marvin_enrollment/include firmware/components/marvin_journal/journal.c tests/firmware/journal_test.c -o work/board/journal-test
UBSAN_OPTIONS=halt_on_error=1 work/board/journal-test
cc $common -I tests/firmware/host-http -I tests/firmware/host-identity -I firmware/main -I "$mbed/include" -I "$json" firmware/main/enrollment_client.c tests/firmware/enrollment_client_test.c work/firmware-ticket-vectors/cjson.o work/mbedtls-host/library/libmbedcrypto.a -o work/board/enrollment-client-test
UBSAN_OPTIONS=halt_on_error=1 work/board/enrollment-client-test
cc $common -Dgettimeofday=owner_test_time -I tests/firmware/host-owner -I tests/firmware/host-identity -I firmware/main -I firmware/components/marvin_journal/include -I firmware/components/marvin_transfer/include -I firmware/components/marvin_enrollment/include -I "$mbed/include" -I "$json" firmware/main/owner_setup.c firmware/components/marvin_journal/journal.c firmware/components/marvin_transfer/transfer.c firmware/components/marvin_enrollment/ticket.c tests/firmware/owner_setup_test.c work/firmware-ticket-vectors/cjson.o work/mbedtls-host/library/libmbedcrypto.a -o work/board/owner-setup-test
UBSAN_OPTIONS=halt_on_error=1 work/board/owner-setup-test work/firmware-ticket-vectors/public.pem work/firmware-ticket-vectors/cases.json
