#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mkdir -p work/board
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined \
  -I tests/firmware/host-gear -I tests/firmware/host-identity -I firmware/main \
  firmware/main/gear_vr_controller.c tests/firmware/gear_vr_controller_test.c \
  -o work/board/gear-vr-controller-test
UBSAN_OPTIONS=halt_on_error=1 work/board/gear-vr-controller-test
