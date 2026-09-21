#!/bin/sh
# Explicitly separate from signed/release builds. This is the quick daily loop.
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
# Local development is quick to flash, but must remain feature-complete with
# the Pet profile (including the ESP-SR AFE and wake model dependency).
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
cd "$project_dir/firmware"
# Profiles are inputs, not an editable saved configuration. Regenerate this
# derived file so a prior default cannot silently override a safety fix.
rm -f "$project_dir/firmware/sdkconfig.waveshare-local-dev.generated"
idf.py -B "$project_dir/build-waveshare-local-dev" -D MARVIN_ENABLE_AFE=ON -D SDKCONFIG=sdkconfig.waveshare-local-dev.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-afe-audible.defaults;profiles/waveshare-local-dev.defaults;profiles/waveshare-afe-v3.defaults' reconfigure build
grep -qx 'CONFIG_MARVIN_LOCAL_DEV_MODE=y' sdkconfig.waveshare-local-dev.generated
grep -qx 'CONFIG_BT_NIMBLE_HOST_TASK_STACK_SIZE=8192' sdkconfig.waveshare-local-dev.generated
grep -qx 'CONFIG_MARVIN_LOCAL_AFE=y' sdkconfig.waveshare-local-dev.generated
grep -qx 'CONFIG_MARVIN_WAKE_AUTOSTART=y' sdkconfig.waveshare-local-dev.generated
grep -qx 'CONFIG_MARVIN_CONNECTION_EYES_ANIMATION=y' sdkconfig.waveshare-local-dev.generated
grep -qx '# CONFIG_MARVIN_CONNECTION_TONE is not set' sdkconfig.waveshare-local-dev.generated
grep -qx 'CONFIG_MARVIN_AFE_LAYOUT_V3=y' sdkconfig.waveshare-local-dev.generated
if grep -qx 'CONFIG_MARVIN_SIGNED_OTA=y' sdkconfig.waveshare-local-dev.generated; then
  echo 'local-dev must not enable signed OTA' >&2
  exit 1
fi
image_bytes=$(wc -c < "$project_dir/build-waveshare-local-dev/marvin.bin")
if [ $((image_bytes * 10)) -gt $((0x400000 * 9)) ]; then
  echo 'Local development image exceeds the 10% afe-v3 slot headroom guard' >&2
  exit 1
fi
