#!/bin/sh
# Advertise the Marvin HTTPS service on the current LAN. The machine itself
# must already resolve as marvin.local (macOS: set its LocalHostName to
# "marvin"; Linux: configure Avahi with host-name=marvin). This script does
# not modify system DNS, certificates, or device trust.
set -eu
port=${MARVIN_LOCAL_PORT:-8443}
case "$port" in *[!0-9]*|'') echo 'MARVIN_LOCAL_PORT must be numeric' >&2; exit 2;; esac
if command -v dns-sd >/dev/null 2>&1; then
  exec dns-sd -R Marvin _marvin._tcp local "$port" path=/api/health origin=https://marvin.local:"$port"
fi
if command -v avahi-publish-service >/dev/null 2>&1; then
  exec avahi-publish-service -s Marvin _marvin._tcp "$port" path=/api/health origin=https://marvin.local:"$port"
fi
echo 'Neither dns-sd nor avahi-publish-service is installed.' >&2
exit 127
