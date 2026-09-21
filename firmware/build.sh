#!/usr/bin/env bash
#
# Build the Marvin firmware.
#
#   ./build.sh                    both environments
#   ./build.sh esp32c3-supermini  one of them
#
# Building both is the default on purpose: voice code is guarded by
# MARVIN_VOICE and the two boards differ in core count and pin map, so it is
# easy to break one while working on the other.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

if [[ $# -gt 0 ]]; then
  exec pio run -e "$1"
fi
exec pio run
