# Local development and demos

Local development is deliberately separate from cloud deployment. It uses a
stable local **name**, not a laptop's transient DHCP address:

```
Browser + Desktop Pet -> https://marvin.local:8443 -> local Caddy -> Marvin
```

`marvin.local` is both the browser origin and the device's pinned enrollment,
health and WebSocket hostname. ESP-IDF's lwIP configuration resolves `.local`
through mDNS on every connection. The changing IP address is therefore not
configuration and must never be written to factory NVS.

## One-time workstation setup

1. Configure the development computer's mDNS name as `marvin` (which exposes
   `marvin.local`). On macOS, set **LocalHostName** in Sharing; on Linux, set
   Avahi's `host-name=marvin` and restart Avahi. Confirm from a second device
   that `marvin.local` resolves to the current computer address.
2. Create a private durable Caddy directory outside the checkout and set
   `MARVIN_TLS_DATA_DIR` to its absolute path. This preserves the Caddy local
   CA across container recreation. Never use `docker compose down -v` for it.
3. Set `APP_ORIGIN=https://marvin.local:8443`,
   `MARVIN_TLS_NAME=marvin.local`, `MARVIN_BIND_IP=0.0.0.0`, and
   `DEPLOYMENT_MODE=local-dev` in the private environment/deployment
   configuration. `MARVIN_BIND_IP=0.0.0.0` is required: it lets the Pet reach
   the local HTTPS proxy from Wi-Fi while TLS, device identity, and enrollment
   still authorize every connection. Start the supplied discovery helper while
   demoing: `sh scripts/local-dev-discovery.sh`.
4. Export and trust Caddy's **public root** certificate once in the browser
   and provision that same public root into the development factory image.
   The device verifies the certificate name `marvin.local`; it does not trust
   an IP address. Never copy the CA private key to a device.
5. Create the factory enrollment/probe values using
   `https://marvin.local:8443`, not a numeric address. This is a one-time
   factory configuration change when moving from the old IP-based setup.

### Migrating an existing Pet to the stable local name

An app-only flash intentionally preserves factory trust, so it cannot repair a
Pet whose factory image still pins an old IP address. Create a new private
factory-input directory from the existing reviewed factory inputs, then use
the supplied tool to write the stable origin, the local Caddy public root, and
the deployment enrollment public key. Do not edit the original factory input
in place and do not copy a private key into the image:

```sh
python3 firmware/tools/bench-trust.py \
  --factory /private-copy/factory-owner-DEVICE-marvin-local \
  --origin https://marvin.local:8443 \
  --ca /absolute/path/to/caddy/pki/authorities/local/root.crt \
  --enrollment-public-key /private-inputs/owner-deployment-public.pem
python3 firmware/tools/board-flash.py --profile local-dev \
  --factory /private-copy/factory-owner-DEVICE-marvin-local \
  --backup VERIFIED_BACKUP --port /dev/cu.usbmodem... --flash
```

The flashing tool shows a write plan and verifies the board, backup, and every
image before it writes. This one-time full local-dev install changes factory
trust but does not program eFuses. Point both local-dev cardless environment
variables at the new private factory directory afterward; its device public
key and setup card remain the same identity.

Some guest Wi-Fi/hotspots suppress multicast. For those networks, arrange a
DNS record for the same hostname (for example `marvin.home.arpa`) and issue a
matching certificate; do not substitute a discovered numeric IP. The hostname,
CA, and enrollment key remain the trust boundary. mDNS is only location
discovery.

## Development-only cardless setup

`LOCAL_DEV_SETUP_CARDS_FILE` may point to the existing factory-generated,
mode-0600 `setup-secret.json` file:

```json
{"deviceId":"marvin_...","username":"...","password":"..."}
```

For a shared developer registry, a one-item JSON array with that object is
also accepted. In Compose, keep the source below `MARVIN_PRIVATE_DIR` and use
its container path (for example
`/private/factory-owner-DEVICE/setup-secret.json`), not the host path.
Set `LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE` to the matching
`/private/factory-owner-DEVICE/device-public.pem`. At local-dev startup Marvin
registers that factory identity in its local database and checks that its
fingerprint matches the setup card. This is deliberately restricted to the
explicit local-development factory input; cloud and local-secure deployments
must use their reviewed operator registration procedure.
The native factory file may additionally contain `security: 2` and `patch: 1`;
these are validated and retained only for compatibility with the factory tool.

With `DEPLOYMENT_MODE=local-dev` and `AUTH_MODE=local`, an authenticated portal session can obtain
that record only in memory immediately before the existing Security2 handshake.
It removes card transcription; it does not remove encrypted BLE, SRP proof,
ticket verification, or TLS. The server rejects this option in `local-secure`
and `cloud` modes. Do not put the file in the repository or browser storage.

## Start or restart the local service

Keep the private environment file and the private deployment-input directory
outside version control. The latter contains `owner-deployment-keys.json` and,
optionally, the cardless setup record. Compose expands its own variables before
it reads the application's environment file, so provide both paths on every
Compose invocation:

```sh
MARVIN_ENV_FILE=/absolute/path/to/.env \
MARVIN_PRIVATE_DIR=/absolute/path/to/private-deployment-inputs \
APP_ORIGIN=https://marvin.local:8443 \
MARVIN_TLS_NAME=marvin.local \
MARVIN_BIND_IP=0.0.0.0 \
MARVIN_TLS_DATA_DIR=/absolute/path/to/marvin-tls \
docker compose -f deploy/compose.yaml up --build -d
```

For this workstation, the application variables in the private `.env` select
`DEPLOYMENT_MODE=local-dev`, `HARDWARE_PROVISIONING_ENABLED=true`,
`ENROLLMENT_KEYS_FILE=/private/owner-deployment-keys.json`, and
`DEVICE_PUBLIC_ORIGIN=https://marvin.local:8443`,
`LOCAL_DEV_SETUP_CARDS_FILE=/private/factory-owner-DEVICE/setup-secret.json`,
and `LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE=/private/factory-owner-DEVICE/device-public.pem`.
Do not put the host path for
the key file in `ENROLLMENT_KEYS_FILE`: inside the container it is deliberately
mounted at `/private`. Check readiness with
`curl --insecure https://marvin.local:8443/api/health` and service state with
the same variables followed by `docker compose -f deploy/compose.yaml ps`.

## Fast firmware loop

Build the distinct profile:

```sh
sh firmware/tools/build-waveshare-local-dev.sh
python3 firmware/tools/board-flash.py --profile local-dev --app-only --backup VERIFIED_BACKUP --port /dev/cu.usbmodem... --flash
```

`--app-only` is accepted only for `local-dev`. It still identifies the board,
checks the reviewed build/layout and verifies the application write, but it
does not write factory NVS, Wi-Fi/owner state, bootloader, partition table, or
OTA metadata. It verifies the preserved wake-model partition before writing,
so use one full local-dev install after a wake-model update and use app-only
only for later code-only iterations. It is intentionally incompatible with signed OTA. Use the
existing guarded profiles and full backup/recovery process for `local-secure`
and cloud-release work.

The local profile keeps the full audible on-device AFE and `Hey Marvin` wake
path enabled; it is a functional Pet profile, not a silent simulation. It sets
the NimBLE host task to 8 KB. The default 4 KB stack
cannot complete this build's Security 2 exchange alongside the owner and audio
callbacks, and causes an immediate Pet reboot just after Bluetooth pairing.
The build helper verifies this setting before it produces a flashable image.

If a deliberately disposable local database no longer has the Pet's former
owner record, first migrate the database whenever possible. As a last-resort
bench recovery, the local-dev firmware accepts `r` over its direct USB console:
it clears only the Pet's durable owner/Wi-Fi journal and immediately reboots.
It never changes factory identity or trust, and is not compiled into
local-secure or cloud firmware. Reconnect through the portal afterward and
choose Wi-Fi again.

## Verification matrix

- Change the laptop Wi-Fi or force a DHCP address change: `marvin.local`
  resolves again and the portal/device reconnect without a rebuild or flash.
- A certificate for another name or from another CA fails on the device.
- The cardless endpoint returns 404 outside `local-dev` and requires an
  authenticated session in it.
- Repeated app-only flashes preserve the device identity, owner link, and Wi-Fi
  configuration.
- Complete a browser Bluetooth pairing and enter the Wi-Fi-selection screen
  without a Pet reboot or a reconnect prompt.
