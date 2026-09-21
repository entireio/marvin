#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p work/board
cc -std=c11 -Wall -Wextra -Werror -fsanitize=address,undefined -I firmware/main firmware/main/eyes_render.c tests/firmware/eyes_render_test.c -o work/board/eyes-render-test
UBSAN_OPTIONS=halt_on_error=1 work/board/eyes-render-test
