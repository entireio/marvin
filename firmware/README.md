# ESP32-S3-WROOM firmware — M0 bench spike

Original ESP-IDF **v5.4.2** C firmware targeting `esp32s3`. No previous Arduino/SuperMini firmware or board pin assignments are reused. Conservative baseline: 4 MB flash, no PSRAM required, two 1.875 MB app partitions. Verify the exact WROOM ordering code, carrier, regulator, antenna, USB/UART and peripheral wiring before flashing. Audio, displays, actuators, owner enrollment, updates, and the physical runtime are later milestones; this image configures no actuator GPIO.

## Build

Install Espressif ESP-IDF v5.4.2 and its esp32s3 tools using the official installation workflow, then source `export.sh`:

```sh
idf.py -C firmware build
idf.py -C firmware size
```

For the toolchain installed during this task:

```sh
export IDF_TOOLS_PATH="$PWD/work/idf-tools"
. ./work/esp-idf/export.sh
IDF_COMPONENT_MANAGER=0 idf.py -C firmware build
```

Disabling the optional component manager is supported here because all dependencies are bundled with ESP-IDF. It avoids process enumeration prohibited by this host's sandbox. No SDK source patches were needed. A successful compile is recorded under `tests/acceptance/results/M00`; no board was attached, flashed, or radio-tested.

## Unique setup credentials

The firmware fails closed when the dedicated `factory` NVS partition lacks a 16-byte SRP salt and 384-byte verifier. There is no shared/default setup password. Generate a unique credential per module, in an excluded private directory:

```sh
python firmware/tools/factory.py --output work/device-001
python "$IDF_PATH/components/nvs_flash/nvs_partition_generator/nvs_partition_gen.py" generate work/device-001/factory.csv work/device-001/factory.bin 0x6000
```

The generator creates private files (0600, directory0700) without printing credentials. Retain the setup secret offline with the physical robot. `factory.bin` belongs at **0x12000** in this partition table. Review the carrier/port and existing flash before using `idf.py -C firmware -p PORT flash` and Espressif's `write_flash 0x12000 work/device-001/factory.bin` command. Neither flashing nor irreversible security eFuse programming is automated here. Production protected identity, signed firmware and encrypted NVS require M7/M11 design; the bench NVS is not protected against physical flash extraction.

## Protocol and bench sequence

Each boot opens an authenticated five-minute BLE setup window, then stops BLE. Power cycling is the bench physical-presence gesture. Production reconfiguration must add fresh owner-bound authorization; knowledge of the setup secret alone is intentionally **not** advertised as sufficient for production ownership.

Service `0000ff50-0000-1000-8000-00805f9b34fb`; characteristics use `ff51` version, `ff52` `prov-session`, `ff53` `marvin-control`. Standard Espressif Protocomm Security 2 patch1 provides SRP6a mutual proof and AES-GCM with counter nonces. Version is public; controls are encrypted after authentication. No homemade cryptography. One client session at a time.

Encrypted JSON commands (maximum384 bytes):

- `{"op":"scan"}` starts an asynchronous **robot radio scan**. Poll `status` until `scan_complete`.
- `{"op":"status"}` reports phase/error, scan generation/count, saved-network flag and `linked:false`.
- `{"op":"network","index":0}` returns one robot-observed network, RSSI/channel and security support; bounded to32 observations. Read indices separately to stay below BLE response limits.
- `{"op":"apply","index":0,"scanGeneration":1,"password":"…"}` tests a network from that exact scan. Stale indices, enterprise/WEP/WPA3-only APs and invalid credentials are rejected. Baseline supports visible 2.4 GHz open and WPA2-Personal (including WPA/WPA2 mixed) networks; hidden SSIDs are deferred to M7. UI hidden-network behavior remains simulation only.

`WIFI_STORAGE_RAM` stages the candidate without replacing saved NVS credentials. Success requires DHCP, SNTP clock synchronization, and certificate-verified HTTPS200 from a fixed configured backend health URL. Set **Marvin → probe URL** in `idf.py -C firmware menuconfig`. Empty URL rejects apply. A laptop's localhost is not reachable from the robot; use a robot-reachable HTTPS server. No TLS bypass or redirect following. The result is `network_verified`, **not account linked**. Failed connection/probe/storage restores the previous network, and failed restoration is represented by the failed state rather than a ready claim. Power loss before commit leaves the prior NVS record. Credentials are not logged.

The bench client uses Espressif's Security2 implementation and `bleak` in your activated Python environment:

```sh
python -m pip install -r firmware/tools/requirements.txt
python firmware/tools/bench.py --credentials work/device-001/setup-secret.json
python firmware/tools/bench.py --credentials work/device-001/setup-secret.json --apply
```

It prompts locally for network index/password, never stores the Wi-Fi password, and does not call Marvin's account APIs. Do not turn on verbose security logging. This client and all BLE/radio paths still require physical validation. Browser transport intentionally stops after discovery until reviewed Web Bluetooth Security2 framing and M7 ownership authorization are implemented.

M0 HIL acceptance: ten consecutive encrypted scan/connect cycles, including reconfiguration; bad proof rejection; laptop-only AP absent; wrong password/DHCP/DNS/TLS failure rollback; power-cycle persistence; setup-window closure. Record board ordering code, flash/heap, browser/client OS, AP security/RSSI, timings and every failure. Firmware compilation alone does not satisfy this gate.
