#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p work/board
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main \
  firmware/main/connection_choreography.c tests/firmware/connection_choreography_test.c \
  -o work/board/connection-choreography-test
UBSAN_OPTIONS=halt_on_error=1 work/board/connection-choreography-test
