#!/bin/sh
set -eu
cd "$(dirname "$0")/../.."
mkdir -p work/board
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main firmware/main/battery_level.c tests/firmware/battery_level_test.c -o work/board/battery-level-test
UBSAN_OPTIONS=halt_on_error=1 work/board/battery-level-test
