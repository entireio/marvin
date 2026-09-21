#!/usr/bin/env bash
#
# Build and flash the Marvin firmware.
#
#   ./flash.sh                    the C3 build
#   ./flash.sh esp32s3-supermini  a named environment
#
# The port is found by looking for an Espressif device rather than left to
# PlatformIO's auto-detection, which on macOS happily picks a Bluetooth serial
# port and then spends thirty seconds failing to talk to a pair of headphones.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

ENVIRONMENT="${1:-esp32c3-supermini}"

# Espressif's native USB (VID 303a) enumerates as usbmodem on macOS and ttyACM
# on Linux; boards with a CP210x or CH34x bridge show up as usbserial/ttyUSB.
find_port() {
  local candidates=()
  for pattern in /dev/cu.usbmodem* /dev/cu.usbserial* /dev/cu.wchusbserial* \
                 /dev/cu.SLAB_USBtoUART* /dev/ttyACM* /dev/ttyUSB*; do
    for path in $pattern; do
      [[ -e "$path" ]] && candidates+=("$path")
    done
  done
  printf '%s\n' "${candidates[@]:-}"
}

PORT="$(find_port | head -1)"

if [[ -z "$PORT" ]]; then
  cat >&2 <<'MSG'

error: no ESP32 found on USB.

  Espressif boards appear as /dev/cu.usbmodem* (macOS) or /dev/ttyACM* (Linux).
  Nothing matching is present, which usually means one of:

    - a charge-only USB cable — very common, and the board still lights up
    - the board is plugged into a dock or hub that is not passing it through
    - the connector is not fully seated

  The C3's USB works from ROM, so no firmware state can prevent it appearing.
  If you do need recovery mode, hold BOOT while plugging in.

MSG
  exit 1
fi

echo "Flashing $ENVIRONMENT to $PORT..."
exec pio run -e "$ENVIRONMENT" -t upload --upload-port "$PORT"
