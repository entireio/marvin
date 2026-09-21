#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
rm -f "$project_dir/firmware/sdkconfig.waveshare-motion-v3.generated"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare-motion-v3" -D MARVIN_ENABLE_AFE=ON -D SDKCONFIG=sdkconfig.waveshare-motion-v3.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-afe-audible.defaults;profiles/waveshare-motion.defaults;profiles/waveshare-afe-v3.defaults' reconfigure build
grep -qx 'CONFIG_MARVIN_AFE_LAYOUT_V3=y' sdkconfig.waveshare-motion-v3.generated
image_bytes=$(wc -c < "$project_dir/build-waveshare-motion-v3/marvin.bin")
if [ $((image_bytes * 10)) -gt $((0x400000 * 9)) ]; then
    echo "Motion image exceeds the 10% afe-v3 slot headroom guard" >&2
    exit 1
fi
