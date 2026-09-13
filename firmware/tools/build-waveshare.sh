#!/bin/sh
set -eu
project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
export IDF_TOOLS_PATH="$project_dir/work/idf-tools"
export IDF_COMPONENT_MANAGER=0
. "$project_dir/work/esp-idf/export.sh"
cd "$project_dir/firmware"
idf.py -B "$project_dir/build-waveshare" -D SDKCONFIG=sdkconfig.waveshare.generated -D 'SDKCONFIG_DEFAULTS=sdkconfig.defaults;profiles/waveshare-audio.defaults' build
