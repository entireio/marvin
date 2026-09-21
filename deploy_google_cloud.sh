#!/usr/bin/env bash
#
# Deploy the Marvin website (docs/) to Google Cloud Run behind a GitHub sign-in.
#
# Run it from anywhere; it operates on the repository it lives in. It is
# idempotent — running it again redeploys the current working tree and leaves
# existing secrets alone.
#
#   ./deploy_google_cloud.sh                 deploy
#   ./deploy_google_cloud.sh --help          the full list of settings
#
# Settings come from the environment, or from a deploy_google_cloud.env file
# beside this script (gitignored — it names people, not secrets).

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

readonly CONFIG_FILE="deploy_google_cloud.env"
readonly SECRET_SESSION="marvin-session-secret"
readonly SECRET_CLIENT="marvin-github-client-secret"
readonly SECRET_OPENAI="marvin-openai-api-key"
readonly SECRET_GEMINI="marvin-gemini-api-key"

ASSUME_YES=false
ROTATE_SESSION_SECRET=false
SET_CLIENT_SECRET=false
SET_OPENAI_KEY=false
SET_GEMINI_KEY=false

# --------------------------------------------------------------------------
# Output helpers
# --------------------------------------------------------------------------
if [[ -t 1 ]]; then
  bold=$'\033[1m'; red=$'\033[31m'; yellow=$'\033[33m'; green=$'\033[32m'; reset=$'\033[0m'
else
  bold=''; red=''; yellow=''; green=''; reset=''
fi

step() { printf '\n%s==> %s%s\n' "$bold" "$1" "$reset"; }
info() { printf '    %s\n' "$1"; }
warn() { printf '%s    warning: %s%s\n' "$yellow" "$1" "$reset"; }
die()  { printf '%s\nerror: %s%s\n' "$red" "$1" "$reset" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Deploy the Marvin website to Cloud Run behind a GitHub OAuth sign-in.

Usage: ./deploy_google_cloud.sh [options]

Options:
  --yes                     Do not ask for confirmation before deploying.
  --set-client-secret       Store a new GitHub OAuth client secret and redeploy.
                            Use after rotating the secret on GitHub.
  --rotate-session-secret   Generate a new cookie-signing key. This signs every
                            visitor out; it is not done automatically. It also
                            invalidates every robot's device token, because the
                            two are signed with the same key.
  --set-openai-key          Store an OpenAI API key and redeploy.
  --set-gemini-key          Store a Google Gemini API key and redeploy.
  --help                    Show this message.

Settings (environment, or deploy_google_cloud.env beside this script):

  GITHUB_CLIENT_ID        required  Client ID of the GitHub OAuth App.
  PROJECT_ID              required  Google Cloud project. Defaults to the
                                    active gcloud project.
  REGION                            Cloud Run region. Default: us-central1.
  SERVICE                           Cloud Run service name. Default: marvin-site.
  SERVICE_ACCOUNT                   Runtime service account. Defaults to the
                                    project's Compute Engine default.
  BASE_URL                          Public origin, e.g. https://marvin.example.
                                    Only needed behind a custom domain; the
                                    server derives it from the request otherwise.
  SESSION_TTL_HOURS                 How long a sign-in lasts. Default: 12.

Voice — optional; without an AI key the site and controller work but the
robot cannot hold a conversation. Keys are stored in Secret Manager by
--set-openai-key / --set-gemini-key, never in this file.

  AI_PROVIDER                       openai or gemini. Defaults to whichever
                                    has a key stored.
  AI_MODEL                          Override the provider's default model.
  AI_VOICE                          Override the provider's default voice.
  DEVICE_TOKEN_TTL_DAYS             How long a robot's credential lasts.
                                    Default: 365.

Access — at least one of these is required. The server refuses to start
without one, because "any GitHub account" is a public site wearing a login page:

  ALLOWED_GITHUB_USERS              Comma-separated GitHub logins.
  ALLOWED_GITHUB_ORGS               Comma-separated GitHub org logins. Members
                                    of these orgs may read the site.
  ALLOW_ANY_GITHUB_USER=true        Let in every GitHub account. Anyone can make
                                    one in a minute; this is close to public.

First run, in order:

  1. Create a GitHub OAuth App at
     https://github.com/settings/developers -> New OAuth App.
     Any callback URL will do for now; step 3 replaces it.
  2. Put its Client ID in GITHUB_CLIENT_ID and run this script. It will
     prompt once for the client secret and store it in Secret Manager.
  3. Paste the callback URL this script prints back into the OAuth App's
     "Authorization callback URL" field and save.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes|-y)                ASSUME_YES=true ;;
    --set-client-secret)     SET_CLIENT_SECRET=true ;;
    --rotate-session-secret) ROTATE_SESSION_SECRET=true ;;
    --set-openai-key)        SET_OPENAI_KEY=true ;;
    --set-gemini-key)        SET_GEMINI_KEY=true ;;
    --help|-h)               usage; exit 0 ;;
    *)                       usage >&2; die "unknown option: $1" ;;
  esac
  shift
done

# --------------------------------------------------------------------------
# Settings
# --------------------------------------------------------------------------
if [[ -f "$CONFIG_FILE" ]]; then
  info "reading $CONFIG_FILE"
  set -a
  # The config file is written by whoever is deploying, so it does not exist
  # when this script is linted.
  # shellcheck disable=SC1090
  source "./$CONFIG_FILE"
  set +a
fi

REGION="${REGION:-us-central1}"
SERVICE="${SERVICE:-marvin-site}"
SESSION_TTL_HOURS="${SESSION_TTL_HOURS:-12}"
BASE_URL="${BASE_URL:-}"
GITHUB_CLIENT_ID="${GITHUB_CLIENT_ID:-}"
ALLOWED_GITHUB_USERS="${ALLOWED_GITHUB_USERS:-}"
ALLOWED_GITHUB_ORGS="${ALLOWED_GITHUB_ORGS:-}"
ALLOW_ANY_GITHUB_USER="${ALLOW_ANY_GITHUB_USER:-}"

# --------------------------------------------------------------------------
# Preflight
# --------------------------------------------------------------------------
step "Checking prerequisites"

command -v gcloud >/dev/null 2>&1 || die \
  "gcloud is not installed. See https://cloud.google.com/sdk/docs/install"

if ! gcloud auth list --filter=status:ACTIVE --format='value(account)' 2>/dev/null | grep -q .; then
  die "no active gcloud account. Run: gcloud auth login"
fi

PROJECT_ID="${PROJECT_ID:-$(gcloud config get-value project 2>/dev/null || true)}"
[[ -n "$PROJECT_ID" && "$PROJECT_ID" != "(unset)" ]] || die \
  "no project set. Use PROJECT_ID=... or run: gcloud config set project YOUR_PROJECT"

[[ -n "$GITHUB_CLIENT_ID" ]] || die \
  "GITHUB_CLIENT_ID is not set. Create an OAuth App at
       https://github.com/settings/developers
   then re-run with GITHUB_CLIENT_ID=... (see --help)."

# Mirror the server's fail-closed rule here so a misconfiguration is caught
# before it becomes a revision that crash-loops.
if [[ -z "$ALLOWED_GITHUB_USERS" && -z "$ALLOWED_GITHUB_ORGS" && "$ALLOW_ANY_GITHUB_USER" != "true" ]]; then
  die "no access rule set. Give ALLOWED_GITHUB_USERS and/or ALLOWED_GITHUB_ORGS,
   or set ALLOW_ANY_GITHUB_USER=true to admit every GitHub account.
   See --help."
fi

[[ -f Dockerfile && -d docs && -d server ]] || die \
  "run this from the Marvin repository — Dockerfile, docs/ and server/ must all be present"

info "project        $PROJECT_ID"
info "region         $REGION"
info "service        $SERVICE"

PROJECT_NUMBER="$(gcloud projects describe "$PROJECT_ID" --format='value(projectNumber)')" \
  || die "cannot read project $PROJECT_ID. Check the name and your permissions."
SERVICE_ACCOUNT="${SERVICE_ACCOUNT:-${PROJECT_NUMBER}-compute@developer.gserviceaccount.com}"
info "runtime as     $SERVICE_ACCOUNT"

# --------------------------------------------------------------------------
# APIs
# --------------------------------------------------------------------------
step "Enabling APIs (no-op if already on)"
gcloud services enable \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  artifactregistry.googleapis.com \
  secretmanager.googleapis.com \
  --project "$PROJECT_ID"

# --------------------------------------------------------------------------
# Secrets
# --------------------------------------------------------------------------
secret_exists() {
  gcloud secrets describe "$1" --project "$PROJECT_ID" >/dev/null 2>&1
}

# add_secret_version reads the value on stdin, so it never appears in argv,
# in the shell history, or in the process list.
add_secret_version() {
  local name="$1"
  if secret_exists "$name"; then
    gcloud secrets versions add "$name" --project "$PROJECT_ID" --data-file=- >/dev/null
  else
    gcloud secrets create "$name" --project "$PROJECT_ID" \
      --replication-policy=automatic --data-file=- >/dev/null
  fi
}

step "Preparing secrets"

# The cookie-signing key. Generated once and kept: rotating it invalidates every
# session, so it happens only when explicitly asked for.
if $ROTATE_SESSION_SECRET || ! secret_exists "$SECRET_SESSION"; then
  if $ROTATE_SESSION_SECRET && secret_exists "$SECRET_SESSION"; then
    warn "rotating the session key — everyone signed in will be signed out"
  fi
  if command -v openssl >/dev/null 2>&1; then
    KEY="$(openssl rand -base64 48)"
  else
    KEY="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
  fi
  printf '%s' "$KEY" | add_secret_version "$SECRET_SESSION"
  unset KEY
  info "$SECRET_SESSION  generated"
else
  info "$SECRET_SESSION  already set"
fi

# The GitHub OAuth client secret. Typed by you, read with echo off, piped
# straight to Secret Manager.
if $SET_CLIENT_SECRET || ! secret_exists "$SECRET_CLIENT"; then
  [[ -t 0 ]] || die "no terminal to prompt on. Run this interactively to set the client secret."
  printf '\n    Paste the GitHub OAuth App client secret (input hidden).\n'
  printf '    Find it at https://github.com/settings/developers\n'
  printf '    Client secret: '
  read -rs CLIENT_SECRET
  printf '\n'
  [[ -n "$CLIENT_SECRET" ]] || die "no client secret entered"
  # printf, not echo: a trailing newline stored in the secret would be sent to
  # GitHub verbatim and every token exchange would fail with bad_verification.
  printf '%s' "$CLIENT_SECRET" | add_secret_version "$SECRET_CLIENT"
  unset CLIENT_SECRET
  info "$SECRET_CLIENT  stored"
else
  info "$SECRET_CLIENT  already set"
fi

# The AI provider keys. Optional: the site and the controller work without
# them, and a deployment that only ever uses one service should only store one.
store_api_key() {
  local secret="$1" label="$2" url="$3"
  [[ -t 0 ]] || die "no terminal to prompt on. Run this interactively to set the $label key."
  printf '\n    Paste the %s API key (input hidden).\n' "$label"
  printf '    Find it at %s\n' "$url"
  printf '    API key: '
  read -rs API_KEY
  printf '\n'
  [[ -n "$API_KEY" ]] || die "no $label key entered"
  # printf, not echo: a trailing newline would be sent in the Authorization
  # header verbatim and every request would be rejected.
  printf '%s' "$API_KEY" | add_secret_version "$secret"
  unset API_KEY
  info "$secret  stored"
}

if $SET_OPENAI_KEY; then
  store_api_key "$SECRET_OPENAI" "OpenAI" "https://platform.openai.com/api-keys"
fi
if $SET_GEMINI_KEY; then
  store_api_key "$SECRET_GEMINI" "Gemini" "https://aistudio.google.com/apikey"
fi

step "Granting the runtime access to those secrets"
RUNTIME_SECRETS=("$SECRET_SESSION" "$SECRET_CLIENT")
if secret_exists "$SECRET_OPENAI"; then RUNTIME_SECRETS+=("$SECRET_OPENAI"); fi
if secret_exists "$SECRET_GEMINI"; then RUNTIME_SECRETS+=("$SECRET_GEMINI"); fi

for secret in "${RUNTIME_SECRETS[@]}"; do
  # Bound to the individual secret rather than the whole project, so this
  # service account cannot read secrets belonging to anything else.
  gcloud secrets add-iam-policy-binding "$secret" \
    --project "$PROJECT_ID" \
    --member "serviceAccount:${SERVICE_ACCOUNT}" \
    --role roles/secretmanager.secretAccessor \
    --condition=None >/dev/null
  info "$secret  -> secretAccessor"
done

# --------------------------------------------------------------------------
# Deploy
# --------------------------------------------------------------------------
# Cloud Run splits --set-env-vars on commas, and the allowlists are themselves
# comma-separated. The ^@^ prefix switches the delimiter to @ for this argument.
ENV_VARS="GITHUB_CLIENT_ID=${GITHUB_CLIENT_ID}"
ENV_VARS="${ENV_VARS}@SESSION_TTL_HOURS=${SESSION_TTL_HOURS}"
if [[ -n "$ALLOWED_GITHUB_USERS" ]]; then
  ENV_VARS="${ENV_VARS}@ALLOWED_GITHUB_USERS=${ALLOWED_GITHUB_USERS}"
fi
if [[ -n "$ALLOWED_GITHUB_ORGS" ]]; then
  ENV_VARS="${ENV_VARS}@ALLOWED_GITHUB_ORGS=${ALLOWED_GITHUB_ORGS}"
fi
if [[ "$ALLOW_ANY_GITHUB_USER" == "true" ]]; then
  ENV_VARS="${ENV_VARS}@ALLOW_ANY_GITHUB_USER=true"
fi
if [[ -n "$BASE_URL" ]]; then
  ENV_VARS="${ENV_VARS}@BASE_URL=${BASE_URL}"
fi
if [[ -n "${AI_PROVIDER:-}" ]]; then
  ENV_VARS="${ENV_VARS}@AI_PROVIDER=${AI_PROVIDER}"
fi
if [[ -n "${AI_MODEL:-}" ]]; then
  ENV_VARS="${ENV_VARS}@AI_MODEL=${AI_MODEL}"
fi
if [[ -n "${AI_VOICE:-}" ]]; then
  ENV_VARS="${ENV_VARS}@AI_VOICE=${AI_VOICE}"
fi
if [[ -n "${DEVICE_TOKEN_TTL_DAYS:-}" ]]; then
  ENV_VARS="${ENV_VARS}@DEVICE_TOKEN_TTL_DAYS=${DEVICE_TOKEN_TTL_DAYS}"
fi

# Only mount the AI keys that actually exist. Naming a missing secret makes
# Cloud Run refuse the revision outright.
SECRET_MOUNTS="GITHUB_CLIENT_SECRET=${SECRET_CLIENT}:latest,SESSION_SECRET=${SECRET_SESSION}:latest"
if secret_exists "$SECRET_OPENAI"; then
  SECRET_MOUNTS="${SECRET_MOUNTS},OPENAI_API_KEY=${SECRET_OPENAI}:latest"
fi
if secret_exists "$SECRET_GEMINI"; then
  SECRET_MOUNTS="${SECRET_MOUNTS},GEMINI_API_KEY=${SECRET_GEMINI}:latest"
fi

step "Ready to deploy"
if [[ "$ALLOW_ANY_GITHUB_USER" == "true" ]]; then
  warn "access: ANY GitHub account. This is very nearly a public site."
else
  if [[ -n "$ALLOWED_GITHUB_USERS" ]]; then info "users allowed  $ALLOWED_GITHUB_USERS"; fi
  if [[ -n "$ALLOWED_GITHUB_ORGS"  ]]; then info "orgs allowed   $ALLOWED_GITHUB_ORGS";  fi
fi
info "session lasts  ${SESSION_TTL_HOURS}h"

if ! $ASSUME_YES; then
  [[ -t 0 ]] || die "not a terminal; re-run with --yes to deploy non-interactively"
  printf '\n    Deploy %s to %s/%s? [y/N] ' "$SERVICE" "$PROJECT_ID" "$REGION"
  read -r reply
  [[ "$reply" =~ ^[Yy]$ ]] || die "cancelled"
fi

step "Building and deploying (a few minutes on the first run)"
# --allow-unauthenticated refers to Google's own IAM layer, not to the GitHub
# sign-in. It has to be on: with IAM auth enabled, Cloud Run would reject every
# request with a 403 before the container ever saw it, and nobody could reach
# the sign-in page to authenticate in the first place. The container is the gate.
#
# The settings below are shaped by the robot's long-lived WebSocket:
#
#   --timeout 3600      Cloud Run's ceiling, and it applies to a WebSocket as
#                       much as to a page load. The robot reconnects when it is
#                       hit, which is invisible because conversation state does
#                       not outlive a turn.
#   --session-affinity  Keeps a reconnecting robot on the instance that already
#                       knows about it.
#   --min-instances 1   Without it the first "Hey Marvin" after a quiet spell
#                       pays for a cold start, which is the one moment a person
#                       is listening for an answer.
#   --max-instances 1   The live device list and the provider chosen in the
#                       controller are per-instance memory. One instance keeps
#                       them coherent, and one instance at concurrency 80 is far
#                       more than a household needs. Raise both together, and
#                       expect the controller's view to fragment if you do.
#   --memory 512Mi      Audio buffers, plus a TLS session to the AI provider for
#                       every robot in a conversation.
gcloud run deploy "$SERVICE" \
  --source . \
  --project "$PROJECT_ID" \
  --region "$REGION" \
  --service-account "$SERVICE_ACCOUNT" \
  --allow-unauthenticated \
  --port 8080 \
  --cpu 1 \
  --memory 512Mi \
  --min-instances 1 \
  --max-instances 1 \
  --concurrency 80 \
  --timeout 3600 \
  --session-affinity \
  --set-env-vars "^@^${ENV_VARS}" \
  --set-secrets "$SECRET_MOUNTS"

URL="$(gcloud run services describe "$SERVICE" \
  --project "$PROJECT_ID" --region "$REGION" --format='value(status.url)')"

# --------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------
step "Verifying"
if command -v curl >/dev/null 2>&1; then
  code="$(curl -s -o /dev/null -w '%{http_code}' "${URL}/healthz" || echo 000)"
  if [[ "$code" == "200" ]]; then
    printf '%s    health check ok%s\n' "$green" "$reset"
  else
    warn "health check returned $code — check: gcloud run services logs read $SERVICE --region $REGION"
  fi
  code="$(curl -s -o /dev/null -w '%{http_code}' "${URL}/index.html" || echo 000)"
  if [[ "$code" == "302" ]]; then
    printf '%s    the site is gated (signed-out request was redirected)%s\n' "$green" "$reset"
  else
    warn "a signed-out request to /index.html returned $code, expected 302 — verify the gate before sharing the URL"
  fi
else
  info "curl not found, skipping checks"
fi

CALLBACK="${BASE_URL:-$URL}/auth/callback"
cat <<EOF

${bold}Deployed.${reset}

  Site      $URL
  Callback  $CALLBACK

${bold}The callback URL must match GitHub exactly, or sign-in fails.${reset}
Open https://github.com/settings/developers, pick your OAuth App, and set
"Authorization callback URL" to:

  $CALLBACK

Useful afterwards:

  Logs      gcloud run services logs read $SERVICE --region $REGION --project $PROJECT_ID
  Change    edit $CONFIG_FILE, then re-run this script
  Remove    gcloud run services delete $SERVICE --region $REGION --project $PROJECT_ID
EOF
