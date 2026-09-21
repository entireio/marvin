#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
# Recreate the generated config so stale menuconfig values cannot override
# the audible profile (in particular wake autostart and the BLE host stack).
rm -f "$project_dir/firmware/sdkconfig.waveshare-afe-audible.generated"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare-afe-audible" -D MARVIN_ENABLE_AFE=ON -D SDKCONFIG=sdkconfig.waveshare-afe-audible.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-afe-audible.defaults' reconfigure build
