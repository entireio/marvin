#!/usr/bin/env python3
"""Run bounded physical signed-update health failures and verify rollback."""
import argparse
import ctypes
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import serial


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--port", default="/dev/cu.usbmodem1101")
parser.add_argument("--sequence", type=int, required=True)
parser.add_argument("--cycles", type=int, default=10)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
if not 1 <= args.cycles <= 10 or not 1 <= args.sequence < 2**32:
    parser.error("cycles must be 1–10 and sequence must be a positive uint32")


def open_port(deadline: float) -> serial.Serial:
    last = None
    while time.monotonic() < deadline:
        try:
            connection = serial.Serial(args.port, 115200, timeout=0.1, write_timeout=1)
            connection.dtr = False
            connection.rts = False
            return connection
        except (OSError, serial.SerialException) as error:
            last = error
            time.sleep(0.5)
    raise RuntimeError(f"Serial port did not recover: {type(last).__name__}")


def reset_esp_usb() -> None:
    if sys.platform != "darwin":
        raise RuntimeError("Automatic USB recovery is only implemented on macOS")
    library = ctypes.CDLL("/opt/homebrew/lib/libusb-1.0.dylib")
    context = ctypes.c_void_p()
    if library.libusb_init(ctypes.byref(context)) != 0:
        raise RuntimeError("libusb initialization failed")
    try:
        library.libusb_open_device_with_vid_pid.restype = ctypes.c_void_p
        handle = library.libusb_open_device_with_vid_pid(context, 0x303A, 0x1001)
        if not handle:
            raise RuntimeError("Espressif USB-JTAG device is unavailable")
        try:
            if library.libusb_reset_device(ctypes.c_void_p(handle)) != 0:
                raise RuntimeError("Espressif USB-JTAG reset failed")
        finally:
            library.libusb_close(ctypes.c_void_p(handle))
    finally:
        library.libusb_exit(context)
    time.sleep(3)


def read_event(port: serial.Serial, pending: bytes, seconds: float):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            pending += port.read(4096)
        except (OSError, serial.SerialException):
            port.close()
            return None, pending, None
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            text = line.decode(errors="replace")
            if any(marker in text for marker in ("Guru Meditation", "stack overflow", "abort() was called")):
                return {"crash": True}, pending, port
            try:
                return json.loads(text), pending, port
            except (UnicodeError, ValueError):
                continue
    return None, pending, port


def wait_ready(port: serial.Serial, pending: bytes, deadline: float, usb_retry: bool = True):
    online = audio = audible_release = False
    progress = 0
    first_progress = None
    while time.monotonic() < deadline:
        try:
            port.write(b"s")
        except (OSError, serial.SerialException):
            port.close()
            port = open_port(deadline)
            pending = b""
            continue
        until = min(deadline, time.monotonic() + 1)
        while time.monotonic() < until:
            event, pending, current = read_event(port, pending, 0.1)
            if current is None:
                port = open_port(deadline)
                pending = b""
                break
            if not event:
                continue
            if event.get("crash"):
                raise RuntimeError("Recovered application crashed")
            if "uplink" in event:
                online = online or event["uplink"].get("online") is True
            if "audio" in event:
                audio = audio or event["audio"].get("available") is True
                audible_release = audible_release or event["audio"].get("silent") is False
            if "afe" in event:
                value = int(event["afe"].get("processedSamples16k", 0))
                first_progress = value if first_progress is None else first_progress
                progress = max(progress, value)
            if online and audio and audible_release and first_progress is not None and progress - first_progress >= 16000:
                return port, pending, {
                    "online": True,
                    "audio": True,
                    "audibleRelease": True,
                    "afeAdvanced": progress - first_progress,
                }
        time.sleep(0.05)
    if usb_retry:
        if port.is_open:
            port.close()
        reset_esp_usb()
        port = open_port(time.monotonic() + 15)
        return wait_ready(port, b"", time.monotonic() + 45, False)
    raise RuntimeError("Recovered release did not become healthy after one USB reset")


args.output.parent.mkdir(parents=True, exist_ok=True)
trials = []
port = open_port(time.monotonic() + 15)
pending = b""
try:
    for cycle in range(1, args.cycles + 1):
        port, pending, before = wait_ready(port, pending, time.monotonic() + 45)
        port.write(b"w")
        time.sleep(0.2)
        port.reset_input_buffer()
        pending = b""
        started = time.monotonic()
        port.write(f"u{args.sequence}\n".encode())
        stages = []
        selected_at = None
        failed_at = None
        fixture_at = None
        recovered_at = None
        deadline = started + 180
        while time.monotonic() < deadline:
            event, pending, current = read_event(port, pending, 0.25)
            if current is None:
                port = open_port(deadline)
                pending = b""
                continue
            if not event:
                if failed_at is not None and (time.monotonic() - started) * 1000 >= failed_at + 30_000:
                    break
                if selected_at is not None and (time.monotonic() - started) * 1000 >= selected_at + 115_000:
                    break
                continue
            if event.get("crash"):
                raise RuntimeError("Unexpected crash during rollback trial")
            update = event.get("update")
            if update:
                elapsed = round((time.monotonic() - started) * 1000)
                stages.append({"stage": update, "elapsedMs": elapsed})
                if update == "boot_confirmed" and failed_at is None:
                    raise RuntimeError("Unhealthy signed trial image was confirmed")
                if update == "trial_health_reject_fixture":
                    fixture_at = elapsed
                if update == "selected":
                    selected_at = elapsed
                if update == "boot_health_failed":
                    failed_at = elapsed
                elif update == "boot_confirmed" and failed_at is not None:
                    recovered_at = elapsed
                    break
        required = {item["stage"] for item in stages}
        recovery_check_at = round((time.monotonic() - started) * 1000)
        rejection_window_ms = (
            failed_at - selected_at
            if failed_at is not None and selected_at is not None
            else None
        )
        trial_dwell_ms = recovery_check_at - selected_at if selected_at is not None else None
        eligible_for_recovery_check = (
            {"requested", "quiescing", "selected"} <= required
            and selected_at is not None
            and trial_dwell_ms is not None
            and trial_dwell_ms >= 85_000
        )
        after = None
        if eligible_for_recovery_check:
            port, pending, after = wait_ready(port, pending, time.monotonic() + 45)
            if recovered_at is None:
                recovered_at = round((time.monotonic() - started) * 1000)
                stages.append({"stage": "recovered_prior_health", "elapsedMs": recovered_at})
        passed = eligible_for_recovery_check and recovered_at is not None and after is not None
        trial = {
            "cycle": cycle,
            "passed": passed,
            "elapsedSeconds": round(time.monotonic() - started, 3),
            "before": before,
            "after": after,
            "rejectionWindowMs": rejection_window_ms,
            "trialDwellBeforeRecoveryCheckMs": trial_dwell_ms,
            "fixtureMarkerObserved": fixture_at is not None,
            "healthFailureMarkerObserved": failed_at is not None,
            "stages": stages,
        }
        trials.append(trial)
        interim = {
            "status": "running",
            "sequence": args.sequence,
            "requestedCycles": args.cycles,
            "completedCycles": cycle,
            "trials": trials,
        }
        args.output.write_text(json.dumps(interim, indent=2) + "\n")
        print(json.dumps({"cycle": cycle, "passed": passed, "elapsedSeconds": trial["elapsedSeconds"]}), flush=True)
        if not passed:
            raise RuntimeError("Rollback stages were incomplete")
finally:
    if port.is_open:
        try:
            port.write(b"w")
        except (OSError, serial.SerialException):
            pass
        port.close()

result = {
    "completedAt": datetime.now(timezone.utc).isoformat(),
    "status": "finished",
    "passed": len(trials) == args.cycles and all(item["passed"] for item in trials),
    "sequence": args.sequence,
    "requestedCycles": args.cycles,
    "completedCycles": len(trials),
    "trials": trials,
    "scope": "Physical ESP32-S3 signed download, trial boot, forced 90-second health rejection, bootloader rollback, and recovered online/audio/AFE health",
}
args.output.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps({key: value for key, value in result.items() if key != "trials"}))
raise SystemExit(0 if result["passed"] else 1)
