#!/usr/bin/env bash
set -euo pipefail

# Deploy the authenticated Marvin application and realtime server to Cloud Run.
# First run requires GITHUB_CLIENT_ID, GITHUB_CLIENT_SECRET and OPENAI_API_KEY
# in the environment. Later runs reuse their Secret Manager values.

PROJECT_ID="${PROJECT_ID:?Set PROJECT_ID to the Google Cloud project.}"
REGION="${REGION:-us-central1}"
SERVICE="${APP_SERVICE:-marvin-site}"
DOCS_SERVICE="${DOCS_SERVICE:-marvin-docs}"
SQL_INSTANCE="${SQL_INSTANCE:-marvin-db}"
SQL_DATABASE="${SQL_DATABASE:-marvin}"
SQL_USER="${SQL_USER:-marvin_app}"
ARTIFACT_REPOSITORY="${ARTIFACT_REPOSITORY:-marvin}"
RUNTIME_SERVICE_ACCOUNT="${RUNTIME_SERVICE_ACCOUNT:-marvin-runtime}"
RUNTIME_EMAIL="${RUNTIME_SERVICE_ACCOUNT}@${PROJECT_ID}.iam.gserviceaccount.com"
IMAGE_TAG="${IMAGE_TAG:-$(date -u +%Y%m%d-%H%M%S)}"
IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${ARTIFACT_REPOSITORY}/app:${IMAGE_TAG}"

gcloud services enable run.googleapis.com sqladmin.googleapis.com sql-component.googleapis.com secretmanager.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com --project "$PROJECT_ID"

if ! gcloud artifacts repositories describe "$ARTIFACT_REPOSITORY" --location "$REGION" --project "$PROJECT_ID" >/dev/null 2>&1; then
  gcloud artifacts repositories create "$ARTIFACT_REPOSITORY" --repository-format docker --location "$REGION" --project "$PROJECT_ID"
fi
if ! gcloud iam service-accounts describe "$RUNTIME_EMAIL" --project "$PROJECT_ID" >/dev/null 2>&1; then
  gcloud iam service-accounts create "$RUNTIME_SERVICE_ACCOUNT" --display-name "Marvin Cloud Run runtime" --project "$PROJECT_ID"
fi
gcloud projects add-iam-policy-binding "$PROJECT_ID" --member "serviceAccount:${RUNTIME_EMAIL}" --role roles/cloudsql.client --condition=None >/dev/null

if ! gcloud sql instances describe "$SQL_INSTANCE" --project "$PROJECT_ID" >/dev/null 2>&1; then
  gcloud sql instances create "$SQL_INSTANCE" --database-version POSTGRES_16 --edition enterprise --tier db-f1-micro --region "$REGION" --availability-type zonal --storage-type SSD --storage-size 10 --storage-auto-increase --backup-start-time 08:00 --retained-backups-count 7 --deletion-protection --project "$PROJECT_ID"
fi
if ! gcloud sql databases describe "$SQL_DATABASE" --instance "$SQL_INSTANCE" --project "$PROJECT_ID" >/dev/null 2>&1; then
  gcloud sql databases create "$SQL_DATABASE" --instance "$SQL_INSTANCE" --project "$PROJECT_ID"
fi

secret_exists(){ gcloud secrets describe "$1" --project "$PROJECT_ID" >/dev/null 2>&1; }
put_secret(){
  local name="$1" value="$2"
  if ! secret_exists "$name"; then gcloud secrets create "$name" --replication-policy automatic --project "$PROJECT_ID" >/dev/null; fi
  printf '%s' "$value" | gcloud secrets versions add "$name" --data-file=- --project "$PROJECT_ID" >/dev/null
}
require_secret(){
  local name="$1" value="${2:-}"
  if [[ -n "$value" ]]; then put_secret "$name" "$value"; elif ! secret_exists "$name"; then echo "Missing $name. Supply its value on the first deployment." >&2; exit 1; fi
}

require_secret marvin-github-client-id "${GITHUB_CLIENT_ID:-}"
require_secret marvin-github-client-secret "${GITHUB_CLIENT_SECRET:-}"
require_secret marvin-openai-api-key "${OPENAI_API_KEY:-}"

if ! secret_exists marvin-database-url; then
  DATABASE_PASSWORD="$(openssl rand -hex 24)"
  gcloud sql users create "$SQL_USER" --instance "$SQL_INSTANCE" --password "$DATABASE_PASSWORD" --project "$PROJECT_ID"
  CONNECTION_NAME="$(gcloud sql instances describe "$SQL_INSTANCE" --project "$PROJECT_ID" --format='value(connectionName)')"
  ENCODED_PASSWORD="$(node -e 'process.stdout.write(encodeURIComponent(process.argv[1]))' "$DATABASE_PASSWORD")"
  put_secret marvin-database-url "postgresql://${SQL_USER}:${ENCODED_PASSWORD}@localhost/${SQL_DATABASE}?host=/cloudsql/${CONNECTION_NAME}"
  unset DATABASE_PASSWORD ENCODED_PASSWORD
fi

if ! secret_exists marvin-enrollment-keys; then
  PRIVATE_DIRECTORY="$(mktemp -d)"
  trap 'rm -rf "$PRIVATE_DIRECTORY"' EXIT
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$PRIVATE_DIRECTORY/private.pem" 2>/dev/null
  RECEIPT_KEY="$(openssl rand -hex 32)"
  node -e 'const fs=require("fs");process.stdout.write(JSON.stringify({privateKey:fs.readFileSync(process.argv[1],"utf8"),receiptKey:process.argv[2]}))' "$PRIVATE_DIRECTORY/private.pem" "$RECEIPT_KEY" | gcloud secrets create marvin-enrollment-keys --data-file=- --replication-policy automatic --project "$PROJECT_ID" >/dev/null
  unset RECEIPT_KEY
  rm -rf "$PRIVATE_DIRECTORY"
  trap - EXIT
fi

for secret in marvin-github-client-id marvin-github-client-secret marvin-openai-api-key marvin-database-url marvin-enrollment-keys; do
  gcloud secrets add-iam-policy-binding "$secret" --member "serviceAccount:${RUNTIME_EMAIL}" --role roles/secretmanager.secretAccessor --condition=None --project "$PROJECT_ID" >/dev/null
done

CONNECTION_NAME="$(gcloud sql instances describe "$SQL_INSTANCE" --project "$PROJECT_ID" --format='value(connectionName)')"
EXISTING_APP_ORIGIN="$(gcloud run services describe "$SERVICE" --region "$REGION" --project "$PROJECT_ID" --format='value(status.url)' 2>/dev/null || true)"
APP_ORIGIN="${APP_ORIGIN:-${EXISTING_APP_ORIGIN:-https://${SERVICE}-r7vxrettpq-uc.a.run.app}}"
PUBLIC_DOCS_URL="${PUBLIC_DOCS_URL:-$(gcloud run services describe "$DOCS_SERVICE" --region "$REGION" --project "$PROJECT_ID" --format='value(status.url)' 2>/dev/null || true)}"
if [[ -z "$PUBLIC_DOCS_URL" ]]; then echo "Deploy the docs service first or set PUBLIC_DOCS_URL." >&2; exit 1; fi

gcloud builds submit --tag "$IMAGE" --project "$PROJECT_ID" .
gcloud run deploy "$SERVICE" --image "$IMAGE" --region "$REGION" --project "$PROJECT_ID" --platform managed --service-account "$RUNTIME_EMAIL" --allow-unauthenticated --ingress all --port 8080 --cpu 2 --memory 2Gi --concurrency 80 --min-instances 1 --max-instances 1 --session-affinity --timeout 3600 --add-cloudsql-instances "$CONNECTION_NAME" --set-env-vars "^@^NODE_ENV=production@DEPLOYMENT_MODE=cloud@AUTH_MODE=github@GITHUB_ALLOWED_USERS=spedemon,ashtom@APP_ORIGIN=${APP_ORIGIN}@PUBLIC_DOCS_URL=${PUBLIC_DOCS_URL}@MODEL_PROVIDER=openai@OPENAI_MODEL=gpt-5.4-mini-2026-03-17@VOICE_PROVIDER=openai@OPENAI_REALTIME_MODEL=gpt-realtime-2.1@OPENAI_TRANSCRIPTION_MODEL=gpt-4o-mini-transcribe@OPENAI_VOICE=cedar@VOICE_EFFECT=subtle-robotic@HARDWARE_PROVISIONING_ENABLED=true@ENROLLMENT_KEYS_FILE=/secrets/enrollment.json@DEVICE_PUBLIC_ORIGIN=${APP_ORIGIN}@TRUST_PROXY_HOPS=0" --set-secrets "GITHUB_CLIENT_ID=marvin-github-client-id:latest,GITHUB_CLIENT_SECRET=marvin-github-client-secret:latest,OPENAI_API_KEY=marvin-openai-api-key:latest,DATABASE_URL=marvin-database-url:latest,/secrets/enrollment.json=marvin-enrollment-keys:latest" --startup-probe "httpGet.path=/api/health,initialDelaySeconds=0,timeoutSeconds=5,periodSeconds=5,failureThreshold=24" --liveness-probe "httpGet.path=/api/health,initialDelaySeconds=5,timeoutSeconds=5,periodSeconds=30,failureThreshold=3"

DEPLOYED_URL="$(gcloud run services describe "$SERVICE" --region "$REGION" --project "$PROJECT_ID" --format='value(status.url)')"
if [[ "$DEPLOYED_URL" != "$APP_ORIGIN" ]]; then echo "APP_ORIGIN does not match the deployed service URL: $DEPLOYED_URL" >&2; exit 1; fi
curl --fail --silent --show-error "$DEPLOYED_URL/api/health"
printf '\nApp deployed: %s\n' "$DEPLOYED_URL"
