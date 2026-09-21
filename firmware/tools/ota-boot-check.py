#!/usr/bin/env python3
"""Verify one bounded signed-image boot without retaining raw serial output."""
import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path

import serial
from esptool.reset import HardReset


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--port", default="/dev/cu.usbmodem1101")
parser.add_argument("--seconds", type=int, default=120)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
if not 10 <= args.seconds <= 180:
    parser.error("seconds must be 10–180")

events = []
pending = b""
confirmed = False
online = False
audio = False
afe_progress = 0
crash = False
started = time.monotonic()
with serial.Serial(args.port, 115200, timeout=0.1, write_timeout=1) as port:
    port.dtr = False
    HardReset(port, uses_usb=True)()
    next_status = started + 2
    while time.monotonic() - started < args.seconds:
        if time.monotonic() >= next_status:
            port.write(b"s")
            next_status = time.monotonic() + 1
        pending += port.read(4096)
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            text = line.decode(errors="replace")
            if any(marker in text for marker in ("Guru Meditation", "stack overflow", "abort() was called")):
                crash = True
            try:
                event = json.loads(text)
            except (UnicodeError, ValueError):
                continue
            if "update" in event:
                events.append({"update": event["update"], "elapsedMs": round((time.monotonic() - started) * 1000)})
                confirmed = confirmed or event["update"] == "boot_confirmed"
            if "uplink" in event:
                online = online or event["uplink"].get("online") is True
            if "audio" in event:
                audio = audio or event["audio"].get("available") is True
            if "afe" in event:
                afe_progress = max(afe_progress, int(event["afe"].get("processedSamples16k", 0)))
        if confirmed and online and audio and afe_progress >= 80000:
            break

result = {
    "completedAt": datetime.now(timezone.utc).isoformat(),
    "passed": confirmed and online and audio and afe_progress >= 80000 and not crash,
    "elapsedSeconds": round(time.monotonic() - started, 3),
    "bootConfirmed": confirmed,
    "deviceOnline": online,
    "audioAvailable": audio,
    "afeProcessedSamples16k": afe_progress,
    "crashObserved": crash,
    "updateEvents": events,
    "scope": "Physical ESP32-S3 boot health and signed-image confirmation; raw serial output not retained",
}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result))
raise SystemExit(0 if result["passed"] else 1)
