#!/bin/sh
set -eu
cd -- "$(dirname "$0")"
./build-app.sh
open '.build/Marvin Simulator.app'
