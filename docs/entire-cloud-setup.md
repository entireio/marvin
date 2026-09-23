# Entire connection deployment

Marvin supports two cloud connection methods. Enable cloud device sign-in when Entire has approved the deployment model; enable the connector fallback when users should be able to keep their Entire login on their own computer.

## Cloud device sign-in

Install and checksum-pin a compatible Entire CLI in the server image. Give the server a private, durable directory that is not web-served or included in ordinary database backups, then configure:

```env
ENTIRE_HOSTED_CLI_PATH=/opt/entire/bin/entire
ENTIRE_HOSTED_SECRET_DIR=/var/lib/marvin/entire-secrets
ENTIRE_HOSTED_SECRET_KEY=<base64-encoded 32-byte key>
```

Generate the key with `openssl rand -base64 32` and store it in the deployment secret manager. Losing the key makes existing Entire connections unrecoverable; exposing it exposes all envelopes on that volume. Rotate it with an explicit re-encryption procedure rather than replacing it in place.

The server process needs read/execute access to the CLI and exclusive read/write access to the secret directory. The CLI needs outbound HTTPS access to Entire. Do not place the directory under the application checkout or static web root.

## Local connector fallback

Enable the relay with:

```env
ENTIRE_CONNECTOR_ENABLED=true
```

The public Marvin origin must be HTTPS so the UI produces a `wss://` connector address. Build the portable core from `apps/entire-connector`, or build the macOS status-bar bundle with:

```sh
npm run entire:connector:macos
```

The macOS release artifact still needs Developer ID signing and notarization before distribution. Linux and Windows can run the same Go core headlessly.

## Operational checks

- Keep the Entire CLI version pinned; run Marvin's checks before every upgrade.
- Persist the encrypted-secret directory across deployments and exclude it from application logs and support bundles.
- Back up the encryption key separately from the encrypted files.
- Terminate TLS at the Marvin origin; never expose the connector endpoint over plaintext except on localhost during development.
- Disconnecting an account deletes its hosted envelope or revokes its connector credential. Entire-side authorization may still need to be revoked separately until Entire exposes a supported revocation command.
