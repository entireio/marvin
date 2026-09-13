# Local deployment packaging (M10/M11 in progress)

The application image builds from the lockfile on Node 22.16.0, runs as UID 1000, and keeps SQLite outside the image in `/data`. It does not contain `.env`, repository checkouts, firmware images, device credentials, or development artifacts. The local Compose stack adds a separate non-root TLS proxy. Both filesystems are read-only apart from declared data volumes and bounded temporary files. No application HTTP port is published. Compose enables exactly one trusted proxy hop so per-client rate limits do not collapse onto the proxy address. Keep the backend port inaccessible to untrusted clients; direct deployments leave `TRUST_PROXY_HOPS=0`.

This packaging does not establish M10/M11 acceptance. The Entire CLI adapter currently needs a native host process with access to its own login/keychain; it is not available inside this image. Hosted multi-user Entire identity and server integration still need an approved interface. Physical enrollment, voice-provider parity, independent installation trials and release acceptance remain open.

## Configure and start

Create an absolute, private environment file outside the checkout (mode 0600). Use the documented server variables in `.env.example`. Set `AUTH_MODE=local` and a hash from `npm run password:hash`, plus a real text provider configuration. Voice is optional and explicitly configured. Do not use development login for LAN access. The file uses literal values; Compose's raw env-file format preserves dollar signs in the password hash. Use Docker Compose supporting `env_file.format: raw`.

From the project root, export `MARVIN_ENV_FILE` with that absolute path and `APP_ORIGIN=https://YOUR-LAN-HOSTNAME:8443`. The hostname must resolve to the server from every client. Set `MARVIN_TLS_NAME` to that hostname or IP without scheme or port, especially for IP-address clients that omit TLS SNI. `MARVIN_BIND_IP` defaults to loopback; set it to the server's specific LAN address when allowing LAN clients. Do not use `localhost` as a robot backend.

Run `docker compose -f deploy/compose.yaml config --quiet`, then `docker compose -f deploy/compose.yaml up -d --build`. Keep these shell variables set for subsequent Compose commands. Do not print the expanded Compose configuration: it includes environment secrets. Check state with `docker compose -f deploy/compose.yaml ps`.

The proxy creates a private local CA and does not install it into the host trust store. Export only its public root certificate using `docker compose -f deploy/compose.yaml cp proxy:/data/caddy/pki/authorities/local/root.crt ./work/marvin-local-root.crt`. Verify its fingerprint through a trusted channel and explicitly trust that certificate on each client that will use the portal. Never export the CA private key to a client. Do not disable certificate validation. The robot also needs an authenticated deployment trust configuration; that firmware path is not yet complete.

Caddy distinguishes local certificates from public ACME certificates, and clients must trust the issuing local CA. See the official [local HTTPS documentation](https://caddyserver.com/docs/automatic-https#local-https) and [reverse proxy documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy). This Compose file uses port 8443 and a private CA; it is not the public DNS/ACME deployment recipe.

## Recovery and updates

Before an application update, take an online SQLite backup through `scripts/backup.ts` with a separately retained 32-byte backup key; include the private configuration file if appropriate. Store the encrypted archive and key separately. The backup tool refuses to overwrite files, verifies restoration integrity, and supports databases up to 256 MB. PostgreSQL requires its own tested backup procedure; do not pass it to the SQLite tool.

Retain the previous image digest and a matching database backup. Schema migrations are forward-only: rolling back the application alone is unsafe after an incompatible migration. Restore into a new volume, verify the restored application privately, and then switch traffic. Never delete the old volume as part of a trial restore. A 24-hour backup schedule, off-machine storage, archive-expiry policy, measured restore exercise, and operator notification still need deployment-specific configuration.

Account deletion removes active application records. Historical backups expire under the operator's documented policy; deleting an account does not rewrite existing encrypted archives. Restore procedures must reapply deletions made after the backup before users regain access. This reconciliation workflow is a release gate, not an implemented automatic guarantee.

## Reproducible non-provider checks

`npx tsx scripts/platform-load.ts --seconds 60 --output work/m11/load-smoke` rehearses 100 authenticated browser event sockets, 100 idle device sockets and 20 synthetic voice sessions. Use 7200 seconds for the two-hour workload. It never reads `.env`, captures room audio, or connects to a model provider. Synthetic 24 kHz PCM exercises transport; 100 text turns are submitted per minute. Report files include errors, concurrency, memory and event-loop samples. Clients and server share one process, so memory/CPU and request-acceptance timing are not isolated server or real-model latency measurements. A short rehearsal cannot pass the two-hour gate.

`npx tsx scripts/recovery-drill.ts` creates 100 synthetic conversations and 1,000 completed turns, snapshots SQLite and fixture configuration, restores to a new directory, restarts the application and compares canonical rows. Temporary fixture data and the in-memory encryption key are removed afterward. Results are written to `work/m11/recovery/results.json`. The measured local fixture is not a substitute for off-machine retrieval, traffic switching, PostgreSQL recovery, post-backup deletion reconciliation or a deployed backup schedule.
