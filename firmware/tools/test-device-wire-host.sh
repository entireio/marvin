#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
# Run test-tickets-host.sh first to prepare cJSON and host crypto dependencies.
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main -I work/esp-idf/components/json/cJSON firmware/main/device_wire.c tests/firmware/device_wire_test.c work/firmware-ticket-vectors/cjson.o -o work/board/device-wire-test
UBSAN_OPTIONS=halt_on_error=1 work/board/device-wire-test
