# Staged firmware rollout

This directory is copied into the Cloud Run image, but generated rollout files
are ignored by Git. To publish an operator-signed release, stage exactly:

- `rollout.json`, pointing at `/app/firmware-rollout/public.pem` and
  `/app/firmware-rollout`
- `public.pem`
- `manifest.bin`
- `image.bin`

`deploy/cloud/app.sh` enables `FIRMWARE_ROLLOUT_FILE` only when the staged
configuration exists and refuses incomplete bundles. Never place a private key
here.
