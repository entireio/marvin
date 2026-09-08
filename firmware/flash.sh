#!/usr/bin/env bash
# Build and flash the PlatformIO project

echo "Flashing Marvin firmware..."
pio run -t upload
