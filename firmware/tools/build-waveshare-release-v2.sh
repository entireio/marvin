#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
rm -rf "$project_dir/build-waveshare-release-v2"
rm -f "$project_dir/firmware/sdkconfig.waveshare-release-v2.generated"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare-release-v2" -D MARVIN_ENABLE_AFE=ON -D SDKCONFIG=sdkconfig.waveshare-release-v2.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-release.defaults;profiles/waveshare-afe-v2.defaults' reconfigure build
grep -qx 'CONFIG_MARVIN_AFE_LAYOUT_V2=y' sdkconfig.waveshare-release-v2.generated
image_bytes=$(wc -c < "$project_dir/build-waveshare-release-v2/marvin.bin")
if [ $((image_bytes * 10)) -gt $((0x300000 * 9)) ]; then
    echo "Release image exceeds the 10% afe-v2 slot headroom guard" >&2
    exit 1
fi
