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
parser.add_argument("--expected-mac", required=True)
parser.add_argument("--attest-prior-healthy", action="store_true")
parser.add_argument("--clear-runtime-nvs", action="store_true", help="Erase saved owner and Wi-Fi state during a deployment move")
parser.add_argument("--attest-source-unlinked", action="store_true", help="Confirm the source backend binding was revoked before clearing runtime state")
parser.add_argument("--flash", action="store_true")
args = parser.parse_args()

if not re.fullmatch(r"/dev/cu\.usbmodem[\w.-]+", args.port):
    parser.error("Select the Waveshare USB modem port")
if not re.fullmatch(r"[0-9a-f]{2}(?::[0-9a-f]{2}){5}", args.expected_mac):
    parser.error("--expected-mac must be a lower-case hardware MAC")
if not args.attest_prior_healthy:
    parser.error("--attest-prior-healthy is required after physical validation of the prior image")
if args.clear_runtime_nvs and not args.attest_source_unlinked:
    parser.error("--attest-source-unlinked is required before clearing runtime NVS")

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
    "#define CONFIG_MARVIN_WAKE_AUTOSTART 1",
    "#define CONFIG_BT_NIMBLE_HOST_TASK_STACK_SIZE 8192",
):
    if required not in config:
        raise RuntimeError(f"Release build lacks required setting: {required}")
if "#define CONFIG_MARVIN_SILENT_TEST 1" in config:
    raise RuntimeError("Release build unexpectedly disables audible behavior")
if "#define CONFIG_MARVIN_AFE_LAYOUT_V3 1" in config:
    layout, app_bytes, candidate_offset, model_offset = "afe-v3", 0x400000, 0x420000, 0x820000
else:
    layout, app_bytes, candidate_offset, model_offset = "afe-v1", 0x1E0000, 0x200000, 0x3E0000

image = args.release / "image.bin"
manifest = args.release / "manifest.bin"
release_metadata = json.loads((args.release / "release.json").read_text())
public_pem = public_release_key(args.public_key)
capsule = bootstrap_capsule(manifest, public_pem)
candidate = image.read_bytes()
if (
    not 1024 <= len(candidate) <= app_bytes
    or candidate[0] != 0xE9
    or int.from_bytes(capsule[12:16], "big") != len(candidate)
    or capsule[16:48] != hashlib.sha256(candidate).digest()
    or release_metadata.get("sequence") != int.from_bytes(capsule[8:12], "big")
    or release_metadata.get("minimumSequence") != 0
    or release_metadata.get("layout") != layout
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
model_digest = digest(flash[model_offset : model_offset + 0x400000])
if flash[model_offset : model_offset + 0x400000] == bytes([0xFF]) * 0x400000:
    raise RuntimeError("Snapshot has no AFE model partition to preserve")

selector = ota_data()
selector_path = args.release / "bootstrap-otadata.bin"
if selector_path.exists() and selector_path.read_bytes() != selector:
    raise RuntimeError("Existing bootstrap OTA selector differs")
if not selector_path.exists():
    with selector_path.open("xb") as output:
        os.fchmod(output.fileno(), 0o600)
        output.write(selector)

nvs_erase_path = args.release / "runtime-nvs-erased.bin"
if args.clear_runtime_nvs:
    erased = bytes([0xFF]) * 0x6000
    if nvs_erase_path.exists() and nvs_erase_path.read_bytes() != erased:
        raise RuntimeError("Existing runtime NVS erase image differs")
    if not nvs_erase_path.exists():
        with nvs_erase_path.open("xb") as output:
            os.fchmod(output.fileno(), 0o600)
            output.write(erased)

plan = {
    "operation": "stage first rollback-capable signed release",
    "port": args.port,
    "expectedMac": args.expected_mac,
    "snapshotSha256": snapshot_manifest["sha256"],
    "priorApplicationSha256": digest(prior),
    "candidateApplicationSha256": digest(candidate),
    "preservedModelPartitionSha256": model_digest,
    "releaseSequence": release_metadata["sequence"],
    "writes": {
        "0x0": str(bootloader.resolve()),
        **({"0x9000": "erase runtime NVS after source unlink attestation"} if args.clear_runtime_nvs else {}),
        "0x12000": str(factory.resolve()),
        hex(candidate_offset): str(image.resolve()),
        "0x10000-last": str(selector_path.resolve()),
    },
    "preserved": ["partition table", "PHY data", "slot 0 prior app", "AFE model"] + ([] if args.clear_runtime_nvs else ["runtime NVS"]),
    "sourceUnlinkAttested": args.attest_source_unlinked if args.clear_runtime_nvs else None,
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

identity = subprocess.run(base + ["--after", "no_reset", "chip_id"], check=True, timeout=60, capture_output=True, text=True)
observed = re.findall(r"MAC:\s*([0-9a-f:]{17})", identity.stdout + identity.stderr, re.IGNORECASE)
if not observed or observed[-1].lower() != args.expected_mac:
    raise RuntimeError("Connected board MAC does not match the reviewed flash plan")
run(["verify_flash", "0x20000", str(args.prior_app)], 180)
run(["verify_flash", "0x8000", str(partition)], 180)
if args.clear_runtime_nvs:
    run(["--after", "no_reset", "write_flash", "--flash_mode", "dio", "--flash_size", "16MB", "--flash_freq", "80m", "0x9000", str(nvs_erase_path)], 120)
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
        hex(candidate_offset),
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
