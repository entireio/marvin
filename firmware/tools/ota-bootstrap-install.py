#!/usr/bin/env python3
"""Stage the first rollback-capable signed image while preserving a verified prior app.

This operator tool never creates a signing key, changes eFuses, erases NVS, or
marks an untested image valid. It requires a fresh double-read board snapshot
and writes the boot selector last.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from ota_metadata import NEW, VALID, entry, inspect
from release_key import bootstrap_capsule, public_release_key


FLASH_BYTES = 0x1000000
APP_BYTES = 0x1E0000
FACTORY_BYTES = 0x6000


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_digest(path: Path) -> str:
    with path.open("rb") as stream:
        value = hashlib.file_digest(stream, "sha256")
    return value.hexdigest()


def ota_data() -> bytes:
    first = entry(1, VALID) + bytes([0xFF]) * (4096 - 32)
    second = entry(2, NEW) + bytes([0xFF]) * (4096 - 32)
    result = first + second
    parsed = inspect(result)
    if parsed["selectedSlot"] != 1 or parsed["records"][0]["state"] != VALID:
        raise RuntimeError("Generated OTA selector did not preserve slot 0 as valid")
    return result


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--snapshot", type=Path, required=True)
parser.add_argument("--build", type=Path, required=True)
parser.add_argument("--release", type=Path, required=True)
parser.add_argument("--public-key", type=Path, required=True)
parser.add_argument("--factory", type=Path, required=True)
parser.add_argument("--prior-app", type=Path, required=True)
parser.add_argument("--port", default="/dev/cu.usbmodem1101")
parser.add_argument("--attest-prior-healthy", action="store_true")
parser.add_argument("--flash", action="store_true")
args = parser.parse_args()

if not re.fullmatch(r"/dev/cu\.usbmodem[\w.-]+", args.port):
    parser.error("Select the Waveshare USB modem port")
if not args.attest_prior_healthy:
    parser.error("--attest-prior-healthy is required after physical validation of the prior image")

snapshot_manifest = json.loads((args.snapshot / "manifest.json").read_text())
snapshot = args.snapshot / "original-flash.bin"
if (
    snapshot_manifest.get("verifiedBySecondRead") is not True
    or snapshot_manifest.get("bytes") != FLASH_BYTES
    or snapshot.stat().st_size != FLASH_BYTES
    or file_digest(snapshot) != snapshot_manifest.get("sha256")
):
    raise RuntimeError("A fresh intact double-read 16 MB snapshot is required")

config = (args.build / "config/sdkconfig.h").read_text()
for required in (
    "#define CONFIG_MARVIN_SIGNED_OTA 1",
    "#define CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE 1",
    "#define CONFIG_MARVIN_HEY_MARVIN_WAKE 1",
):
    if required not in config:
        raise RuntimeError(f"Release build lacks required setting: {required}")
if "#define CONFIG_MARVIN_SILENT_TEST 1" in config:
    raise RuntimeError("Release build unexpectedly disables audible behavior")

image = args.release / "image.bin"
manifest = args.release / "manifest.bin"
release_metadata = json.loads((args.release / "release.json").read_text())
public_pem = public_release_key(args.public_key)
capsule = bootstrap_capsule(manifest, public_pem)
candidate = image.read_bytes()
if (
    not 1024 <= len(candidate) <= APP_BYTES
    or candidate[0] != 0xE9
    or int.from_bytes(capsule[12:16], "big") != len(candidate)
    or capsule[16:48] != hashlib.sha256(candidate).digest()
    or release_metadata.get("sequence") != int.from_bytes(capsule[8:12], "big")
    or release_metadata.get("minimumSequence") != 0
    or release_metadata.get("layout") != "afe-v1"
):
    raise RuntimeError("Release bundle is inconsistent with its signed bootstrap capsule")

build_image = args.build / "marvin.bin"
bootloader = args.build / "bootloader/bootloader.bin"
partition = args.build / "partition_table/partition-table.bin"
factory = args.factory / "factory.bin"
for path in (build_image, bootloader, partition, factory, args.prior_app):
    if not path.is_file():
        raise RuntimeError(f"Missing installation input: {path}")
if build_image.read_bytes() != candidate:
    raise RuntimeError("Signed image differs from the reviewed release build")
if factory.stat().st_size != FACTORY_BYTES:
    raise RuntimeError("Factory image has an unexpected size")

flash = snapshot.read_bytes()
prior = args.prior_app.read_bytes()
if flash[0x20000 : 0x20000 + len(prior)] != prior:
    raise RuntimeError("Snapshot slot 0 does not contain the attested prior application")
if flash[0x8000 : 0x8000 + partition.stat().st_size] != partition.read_bytes():
    raise RuntimeError("Release partition table differs from the board snapshot")
model_digest = digest(flash[0x3E0000 : 0x7E0000])
if flash[0x3E0000 : 0x7E0000] == bytes([0xFF]) * 0x400000:
    raise RuntimeError("Snapshot has no AFE model partition to preserve")

selector = ota_data()
selector_path = args.release / "bootstrap-otadata.bin"
if selector_path.exists() and selector_path.read_bytes() != selector:
    raise RuntimeError("Existing bootstrap OTA selector differs")
if not selector_path.exists():
    with selector_path.open("xb") as output:
        os.fchmod(output.fileno(), 0o600)
        output.write(selector)

plan = {
    "operation": "stage first rollback-capable signed release",
    "port": args.port,
    "snapshotSha256": snapshot_manifest["sha256"],
    "priorApplicationSha256": digest(prior),
    "candidateApplicationSha256": digest(candidate),
    "preservedModelPartitionSha256": model_digest,
    "releaseSequence": release_metadata["sequence"],
    "writes": {
        "0x0": str(bootloader.resolve()),
        "0x12000": str(factory.resolve()),
        "0x200000": str(image.resolve()),
        "0x10000-last": str(selector_path.resolve()),
    },
    "preserved": ["partition table", "runtime NVS", "PHY data", "slot 0 prior app", "AFE model"],
    "efusesChanged": False,
}
print(json.dumps(plan, indent=2), flush=True)
if not args.flash:
    print("Plan only. Add --flash to stage the candidate; no serial access occurred.")
    raise SystemExit(0)

base = [
    sys.executable,
    "-m",
    "esptool",
    "--chip",
    "esp32s3",
    "--port",
    args.port,
    "--baud",
    "460800",
]

def run(parts: list[str], timeout: int = 300) -> None:
    subprocess.run(base + parts, check=True, timeout=timeout)

run(["verify_flash", "0x20000", str(args.prior_app)], 180)
run(["verify_flash", "0x8000", str(partition)], 180)
run(
    [
        "--after",
        "no_reset",
        "write_flash",
        "--flash_mode",
        "dio",
        "--flash_size",
        "16MB",
        "--flash_freq",
        "80m",
        "0x0",
        str(bootloader),
        "0x12000",
        str(factory),
        "0x200000",
        str(image),
    ],
    300,
)
run(
    [
        "--after",
        "hard_reset",
        "write_flash",
        "--flash_mode",
        "dio",
        "--flash_size",
        "16MB",
        "--flash_freq",
        "80m",
        "0x10000",
        str(selector_path),
    ],
    120,
)

result = {
    **plan,
    "completedAt": datetime.now(timezone.utc).isoformat(),
    "esptoolWriteAndVerifySucceeded": True,
    "candidateMarkedNew": True,
    "priorMarkedValid": True,
    "applicationConfirmationPending": True,
}
result_path = args.release / "bootstrap-install.json"
result_path.write_text(json.dumps(result, indent=2) + "\n")
os.chmod(result_path, 0o600)
print(f"Signed candidate staged; application health confirmation is pending: {result_path}")
