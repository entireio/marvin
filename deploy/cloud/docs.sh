#!/usr/bin/env bash
set -euo pipefail

# Deploy the public build documentation independently from the realtime app.

PROJECT_ID="${PROJECT_ID:?Set PROJECT_ID to the Google Cloud project.}"
REGION="${REGION:-us-central1}"
SERVICE="${DOCS_SERVICE:-marvin-docs}"
ARTIFACT_REPOSITORY="${ARTIFACT_REPOSITORY:-marvin}"
IMAGE_TAG="${IMAGE_TAG:-$(date -u +%Y%m%d-%H%M%S)}"
IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${ARTIFACT_REPOSITORY}/docs:${IMAGE_TAG}"

gcloud services enable run.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com --project "$PROJECT_ID"
if ! gcloud artifacts repositories describe "$ARTIFACT_REPOSITORY" --location "$REGION" --project "$PROJECT_ID" >/dev/null 2>&1; then
  gcloud artifacts repositories create "$ARTIFACT_REPOSITORY" --repository-format docker --location "$REGION" --project "$PROJECT_ID"
fi
gcloud builds submit --config deploy/cloud/cloudbuild-docs.yaml --substitutions "_IMAGE=${IMAGE}" --project "$PROJECT_ID" .
gcloud run deploy "$SERVICE" --image "$IMAGE" --region "$REGION" --project "$PROJECT_ID" --platform managed --allow-unauthenticated --ingress all --port 8080 --cpu 1 --memory 256Mi --concurrency 200 --min-instances 0 --max-instances 3 --timeout 60 --startup-probe "httpGet.path=/,initialDelaySeconds=0,timeoutSeconds=3,periodSeconds=5,failureThreshold=12"

DEPLOYED_URL="$(gcloud run services describe "$SERVICE" --region "$REGION" --project "$PROJECT_ID" --format='value(status.url)')"
curl --fail --silent --show-error "$DEPLOYED_URL/" >/dev/null
printf 'Docs deployed: %s\n' "$DEPLOYED_URL"
