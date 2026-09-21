#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
rm -rf "$project_dir/build-waveshare-ota-failure"
rm -f "$project_dir/firmware/sdkconfig.waveshare-ota-failure.generated"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare-ota-failure" \
  -D MARVIN_ENABLE_AFE=ON \
  -D SDKCONFIG=sdkconfig.waveshare-ota-failure.generated \
  -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-release.defaults;profiles/waveshare-ota-failure.defaults' \
  reconfigure build
