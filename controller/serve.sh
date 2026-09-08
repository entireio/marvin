#!/usr/bin/env bash
# Start a local web server for the Marvin Bluetooth Terminal.
# Web Bluetooth works on localhost without HTTPS in Chrome.

echo "Starting Marvin web controller on http://localhost:3000 ..."
npx -y serve -l 3000 .
