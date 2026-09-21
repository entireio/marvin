#!/usr/bin/env bash
#
# Run the whole system on this machine, with Marvin on the same Wi-Fi.
#
# This is the bench setup: no cloud, no GitHub OAuth app, no certificate. The
# backend runs here, the robot connects to this machine's LAN address, and the
# controller is opened at localhost.
#
#   ./run_local.sh
#   ./run_local.sh --provider gemini
#
# The sign-in is off in this mode, and the server refuses any request that did
# not come from localhost or a private address. Do not run it on a public host.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

# The signing key is kept rather than regenerated, because it signs the robots'
# credentials: a fresh key on every start would mean re-provisioning the robot
# every time you restarted the server.
readonly SECRET_FILE=".marvin-local-secret"
PORT="${PORT:-8080}"

if [[ -t 1 ]]; then
  bold=$'\033[1m'; dim=$'\033[2m'; yellow=$'\033[33m'; green=$'\033[32m'; reset=$'\033[0m'
else
  bold=''; dim=''; yellow=''; green=''; reset=''
fi

die() { printf '\nerror: %s\n' "$1" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --provider) export AI_PROVIDER="${2:-}"; shift ;;
    --port)     PORT="${2:-}"; shift ;;
    --help|-h)
      sed -n '2,14p' "$0" | sed 's/^#\{0,1\} \{0,1\}//'
      printf '\nOptions:\n'
      printf '  --provider openai|gemini   Which AI service to use.\n'
      printf '  --port N                   Default 8080.\n\n'
      printf 'Set OPENAI_API_KEY or GEMINI_API_KEY in the environment, or in a\n'
      printf '.env file beside this script, to give Marvin something to answer with.\n'
      exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

command -v go >/dev/null 2>&1 || die "Go is not installed: https://go.dev/dl/"

# A .env here is for convenience on a bench and holds API keys, so it must never
# be committed. .gitignore covers it; this is the reminder.
if [[ -f .env ]]; then
  # shellcheck source=/dev/null  # optional, and gitignored
  set -a; source ./.env; set +a
  printf '%s    settings from .env%s\n' "$dim" "$reset"
fi

if [[ ! -f "$SECRET_FILE" ]]; then
  openssl rand -base64 48 > "$SECRET_FILE"
  chmod 600 "$SECRET_FILE"
  printf '%s    generated %s%s\n' "$dim" "$SECRET_FILE" "$reset"
fi

SESSION_SECRET="$(cat "$SECRET_FILE")"

export MARVIN_LOCAL=true
export SESSION_SECRET
export PORT
# Absolute, because the last line of this script changes directory into
# server/ to run the module, and a relative path would resolve from there.
export DOCS_DIR="${DOCS_DIR:-$PWD/docs}"
export CONTROLLER_DIR="${CONTROLLER_DIR:-$PWD/controller}"

if [[ -z "${OPENAI_API_KEY:-}" && -z "${GEMINI_API_KEY:-}" ]]; then
  printf '\n%s    No AI key set — Marvin will connect and then have nothing to say.%s\n' "$yellow" "$reset"
  printf '%s    export OPENAI_API_KEY=… or GEMINI_API_KEY=…, or put it in .env%s\n' "$yellow" "$reset"
fi

printf '\n%s==> Starting the backend on port %s%s\n' "$bold" "$PORT" "$reset"
printf '    %sOpen the controller at%s %shttp://localhost:%s/app/%s\n' "$dim" "$reset" "$green" "$PORT" "$reset"
printf '    %s(localhost, not the LAN address — Web Bluetooth will not run\n' "$dim"
printf '     anywhere else without HTTPS, and Scan would fail.)%s\n\n' "$reset"

cd server && exec go run .
