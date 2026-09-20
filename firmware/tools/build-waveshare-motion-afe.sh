#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=1
. "$project_dir/work/esp-idf/export.sh"
# Regenerate the profile each time so a previous menuconfig session cannot
# silently drop the wake engine or remote-control configuration.
rm -f "$project_dir/firmware/sdkconfig.waveshare-motion.generated"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare-motion-afe" -D MARVIN_ENABLE_AFE=ON -D SDKCONFIG=sdkconfig.waveshare-motion.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-provisioning.defaults;profiles/waveshare-owner.defaults;profiles/waveshare-afe.defaults;profiles/waveshare-afe-audible.defaults;profiles/waveshare-motion.defaults' reconfigure build
for option in MARVIN_LOCAL_AFE MARVIN_WAKE_AUTOSTART MARVIN_MICRO_WAKE_WORD MARVIN_HEY_MARVIN_WAKE; do
    if ! grep -qx "CONFIG_${option}=y" sdkconfig.waveshare-motion.generated; then
        echo "Missing required motion/audio option: $option" >&2
        exit 1
    fi
done
