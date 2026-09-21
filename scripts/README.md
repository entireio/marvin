# Project scripts

Run TypeScript scripts through the package commands where one exists. The
scripts below are developer/operator tools, not application runtime entry
points. Most write reports under ignored `work/`; retain only sanitized
evidence in source control.

## Everyday commands

| Goal | Command |
| --- | --- |
| Start the portal and API | `npm run dev` |
| Typecheck, unit tests and production build | `npm run check` |
| Run browser tests | `npm run test:e2e` |
| Start local HTTPS packaging | Follow [deployment](../docs/deployment.md) |
| Advertise the local demo service | `sh scripts/local-dev-discovery.sh` |
| Hash a local password | `npm run password:hash` |

`local-dev-discovery.sh` advertises an already-running `marvin.local` service;
it does not configure DNS, certificates, Docker, or the Pet. Stop it with
Ctrl-C when the demo ends.

## Evidence and operator tools

- `backup.ts`, `recovery-drill.ts`, `platform-load.ts`, and `device-soak.ts`
  exercise backup, recovery and load paths. They do not make a deployment
  production-ready by themselves.
- `provider-smoke.ts`, `voice-smoke.ts`, and the browser voice scripts can use
  paid providers. Read their `--help`/source and use private configuration;
  never put a provider key in an argument or committed report.
- `entire-setup.ts`, `entire-smoke.ts`, and `entire-companion.ts` require a
  separately authorized local Entire setup. They are not hosted identity tools.
- `owner-ble-smoke.ts`, `ble-smoke.ts`, and `physical-ui-smoke.ts` are physical
  setup evidence tools. Follow the firmware guide and use only the intended
  board and private factory files.
- Firmware rollout and update vector scripts are release-specific. Consult
  [firmware updates](../docs/m11-firmware-updates.md) before using them.

The current local development and experimental transport boundaries are in the
[current development checkpoint](../docs/current-development.md).
