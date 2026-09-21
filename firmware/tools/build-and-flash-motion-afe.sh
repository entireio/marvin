#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
port=${MARVIN_PORT:-/dev/cu.usbmodem1101}
backup=${MARVIN_BACKUP:-}
factory=${MARVIN_FACTORY:-}

if [ "${MARVIN_FLASH:-}" != "1" ]; then
    echo 'This command builds and flashes hardware. Re-run with MARVIN_FLASH=1 after reviewing the target board and backup.' >&2
    exit 2
fi

if [ -z "$backup" ]; then
    backup=$(find "$project_dir/work/board" -maxdepth 1 -type d -name 'backup-*' -exec test -f '{}/manifest.json' \; -print 2>/dev/null | sort | tail -n 1)
fi
if [ -z "$factory" ]; then
    factory=$(find "$project_dir/work/board" -maxdepth 1 -type d -name 'factory-owner-*' -exec test -f '{}/factory.bin' \; -print 2>/dev/null | sort | tail -n 1)
fi
if [ -z "$backup" ] || [ ! -f "$backup/manifest.json" ]; then
    echo "No verified board backup found. Set MARVIN_BACKUP to one." >&2
    exit 1
fi
if [ -z "$factory" ] || [ ! -f "$factory/factory.bin" ]; then
    echo "No owner factory directory found. Set MARVIN_FACTORY to one." >&2
    exit 1
fi

printf '%s\n' "Building motion-afe for $port" "Using verified backup: $backup" "Verifying and preserving factory: $factory"

sh "$project_dir/firmware/tools/build-waveshare-motion-afe.sh"
"$project_dir/work/idf-tools/python_env/idf5.4_py3.13_env/bin/python" \
    "$project_dir/firmware/tools/board-flash.py" \
    --backup "$backup" \
    --port "$port" \
    --profile motion-afe \
    --factory "$factory" \
    --reuse-verified-base \
    --flash
