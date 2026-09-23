# Pet commissioning, deployment moves, and return recovery

Every physical Pet has one permanent P-256 identity and one Security2 setup
secret. Local and cloud deployments add trust to that identity; they do not
replace it. Keep the private fleet vault outside every repository and back it
up encrypted off-machine.

The open-source repository must remain fully secret-free. For team operation,
do not make a conventional private Git repository the vault: Git history makes
rotation and deletion unreliable. Use this split instead:

- Google Cloud KMS holds non-exportable P-256 firmware-release and fleet-return
  recovery signing keys. Grant signing, administration, and audit roles
  separately.
- Secret Manager holds bounded setup cards, factory identity CSVs, and service
  credentials, one secret per Pet or authority with explicit team IAM.
- A private Cloud Storage bucket with uniform bucket-level access, object
  versioning/retention, audit logs, and KMS encryption holds verified 16 MB
  backups and generated deployment artifacts.
- An optional private `marvin-fleet-ops` repository contains only non-secret
  device manifests, stable Pet names, MAC/device IDs, deployment status,
  checksums, runbooks, and Secret Manager/Cloud Storage resource references.
  If an exceptional secret must be versioned, store only a SOPS envelope tied
  to the team KMS key and enforce a CI secret scan; never commit plaintext.

The local `~/.marvin/fleet` directory is a commissioning cache, not the shared
system of record. Give it mode 0700, keep it outside cloud-sync folders, and
remove exportable signing keys after the KMS import and signer migration have
been verified.

## Private vault

Use `npm run pet -- import ...` once for an assembled board. The command
validates the setup credential against the public identity, creates a missing
copyable setup card without printing it, and writes a non-secret inventory
manifest. `npm run pet -- list --vault PATH` lists identities, while
`npm run pet -- card --vault PATH --device NAME` prints only the private card
file's location. Setup codes are never written to logs or inventory manifests.

Each deployment directory is derived from the canonical identity directory.
It contains that deployment's HTTPS origin and CA, enrollment public key, and
when applicable firmware-release and fleet-recovery public keys. Enrollment,
firmware-signing, and fleet-recovery private keys are separate trust domains.

Local development may generate `LOCAL_DEV_PETS_FILE` from the vault with
`npm run pet -- local-registry ...`. The registry accepts up to 64 active
development Pets and reveals only the selected Pet's setup credential to an
authenticated same-origin local browser session.

## Preparing a new Pet

1. Identify the USB MAC and capture a verified double-read 16 MB backup.
2. Generate its permanent identity once and import it into the fleet vault.
3. Derive local and cloud factory images from the canonical identity.
4. Register the public device identity with both managed deployments. Public
   registration grants no ownership and makes later moves deterministic.
5. Build and review the target profile. Local development uses the explicit
   local profile; cloud uses the signed rollback-enabled release profile.
6. Run the guarded flash in plan mode, confirm the MAC/backup/identity/layout,
   then apply it. Verify setup mode and the exact running image hash.

## Moving between deployments

Never change factory trust while a Pet still contains an owner from the old
deployment. The move operator must checkpoint and verify this order:

1. Match USB MAC, device ID, backup, and source deployment.
2. Revoke the source binding and increment its ownership epoch.
3. While the source trust is still installed, clear the Pet with the source's
   signed reconciliation flow and confirm both backend and Pet are unlinked.
4. Capture a fresh verified backup.
5. Confirm target public-key registration.
6. Flash target factory trust and firmware through the guarded layout/release
   procedure. Preserve the permanent identity.
7. Leave the Pet unlinked and verify that the target portal can begin normal
   setup.

A failure before step 3 must not change factory trust. A failure afterward is
resumed from the recorded checkpoint. No move may claim completion while an
old backend binding remains.

## Returned-device recovery

An operator may recover returned hardware without the former owner's action.
`operator-unlink` is a trusted database-side command, not a public API. It
revokes the exact device binding, advances its epoch, and records operator,
reason, target, former owner, and former epoch in an append-only audit row.
It never grants access to the former owner's account or conversations.

For device-side clearing, every newly prepared factory image includes only the
public fleet-recovery key. The private P-256 recovery key must be imported into
a non-exportable KMS/HSM before production use. A recovery signature covers an
exact device ID, fresh device nonce, current owner and epoch, operator, case,
and a two-minute validity window. Firmware accepts it only inside the existing
authenticated Security2 physical setup session. `clear_returned` erases the
ownership namespace—including owner credential, pending transaction, and saved
Wi-Fi—but preserves factory identity and trust. The Pet must restart before it
is prepared for another deployment.

The temporary `recovery-key` command exists for isolated development and key
ceremony tests. Do not retain an exportable private recovery key as the fleet
production authority. Recovery-key use, rotation, compromise response, and
replacement factory trust require documented audit procedures.

## Live fleet compatibility

Backend lifecycle changes are additive. They must not rotate enrollment keys,
change the application origin, rewrite `body_slots`, `device_epochs`,
`device_credentials`, or `device_identities`, or require a new firmware
protocol from already-linked Pets. Cloud Run replacement may briefly close a
WebSocket; existing firmware reconnects with its durable credential. Test each
release against a copied database containing a linked legacy Pet, and use only
rollback-compatible schema additions until the new revision is observed
serving the existing fleet.
