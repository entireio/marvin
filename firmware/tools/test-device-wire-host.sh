#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
# Run test-tickets-host.sh first to prepare cJSON and host crypto dependencies.
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main -I work/esp-idf/components/json/cJSON firmware/main/device_wire.c tests/firmware/device_wire_test.c work/firmware-ticket-vectors/cjson.o -o work/board/device-wire-test
UBSAN_OPTIONS=halt_on_error=1 work/board/device-wire-test
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main firmware/main/audio_adpcm.c tests/firmware/audio_adpcm_test.c -o work/board/audio-adpcm-test
UBSAN_OPTIONS=halt_on_error=1 work/board/audio-adpcm-test
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main tests/firmware/audio_playback_policy_test.c -o work/board/audio-playback-policy-test
UBSAN_OPTIONS=halt_on_error=1 work/board/audio-playback-policy-test
