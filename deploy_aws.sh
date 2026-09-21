#!/usr/bin/env bash
#
# Deploy the Marvin backend to AWS: ECS Fargate behind an Application Load
# Balancer.
#
# Not App Runner, which is the obvious choice for a container like this and
# cannot carry a WebSocket. The robot's link is a WebSocket held open for as
# long as it has power, so the load balancer has to be one that speaks the
# protocol — which an ALB does and App Runner does not.
#
# The Google deployment in deploy_google_cloud.sh runs the same image with the
# same environment. Nothing in the server knows which cloud it is on.
#
#   ./deploy_aws.sh                 deploy
#   ./deploy_aws.sh --help          the full list of settings
#
# Settings come from the environment, or from a deploy_aws.env file beside this
# script (gitignored).

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

readonly CONFIG_FILE="deploy_aws.env"
readonly SECRET_SESSION="marvin/session-secret"
readonly SECRET_CLIENT="marvin/github-client-secret"
readonly SECRET_OPENAI="marvin/openai-api-key"
readonly SECRET_GEMINI="marvin/gemini-api-key"

ASSUME_YES=false
ROTATE_SESSION_SECRET=false
SET_CLIENT_SECRET=false
SET_OPENAI_KEY=false
SET_GEMINI_KEY=false

# --------------------------------------------------------------------------
# Output helpers
# --------------------------------------------------------------------------
if [[ -t 1 ]]; then
  bold=$'\033[1m'; red=$'\033[31m'; yellow=$'\033[33m'; reset=$'\033[0m'
else
  bold=''; red=''; yellow=''; reset=''
fi

step() { printf '\n%s==> %s%s\n' "$bold" "$1" "$reset"; }
info() { printf '    %s\n' "$1"; }
warn() { printf '%s    warning: %s%s\n' "$yellow" "$1" "$reset"; }
die()  { printf '%s\nerror: %s%s\n' "$red" "$1" "$reset" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Deploy the Marvin backend to AWS ECS Fargate behind an Application Load Balancer.

Usage: ./deploy_aws.sh [options]

Options:
  --yes                     Do not ask for confirmation before deploying.
  --set-client-secret       Store a new GitHub OAuth client secret and redeploy.
  --rotate-session-secret   Generate a new cookie-signing key. This signs every
                            visitor out and invalidates every robot's device
                            token, which are signed with the same key.
  --set-openai-key          Store an OpenAI API key and redeploy.
  --set-gemini-key          Store a Google Gemini API key and redeploy.
  --help                    Show this message.

Settings (environment, or deploy_aws.env beside this script):

  GITHUB_CLIENT_ID        required  Client ID of the GitHub OAuth App.
  CERTIFICATE_ARN         required  ACM certificate for the domain you will
                                    serve from, in the same region. The robot
                                    verifies TLS, so a self-signed certificate
                                    will not do.
  AWS_REGION                        Default: us-east-1.
  CLUSTER                           ECS cluster name. Default: marvin.
  SERVICE                           ECS service name. Default: marvin.
  ECR_REPO                          Image repository. Default: marvin.
  VPC_ID                            Default: the region's default VPC.
  SUBNET_IDS                        Comma-separated. Default: the default VPC's.
  BASE_URL                          Public origin, e.g. https://marvin.example.
                                    Needed here: the ALB sees its own hostname,
                                    not yours.
  SESSION_TTL_HOURS                 How long a sign-in lasts. Default: 12.

Access — at least one is required, exactly as on Google:

  ALLOWED_GITHUB_USERS              Comma-separated GitHub logins.
  ALLOWED_GITHUB_ORGS               Comma-separated org logins.
  ALLOW_ANY_GITHUB_USER             "true" admits every GitHub account.

Voice — optional:

  AI_PROVIDER                       openai or gemini.
  AI_MODEL, AI_VOICE                Override the provider's defaults.
  DEVICE_TOKEN_TTL_DAYS             Default: 365.
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
  set -a
  # The config file is written by whoever is deploying, so it does not exist
  # when this script is linted.
  # shellcheck disable=SC1090
  source "./$CONFIG_FILE"
  set +a
  info "settings from $CONFIG_FILE"
fi

AWS_REGION="${AWS_REGION:-us-east-1}"
CLUSTER="${CLUSTER:-marvin}"
SERVICE="${SERVICE:-marvin}"
ECR_REPO="${ECR_REPO:-marvin}"
SESSION_TTL_HOURS="${SESSION_TTL_HOURS:-12}"

command -v aws >/dev/null 2>&1 || die "the AWS CLI is not installed: https://aws.amazon.com/cli/"
command -v docker >/dev/null 2>&1 || die "docker is not installed; it builds the image"

[[ -n "${GITHUB_CLIENT_ID:-}" ]] || die "GITHUB_CLIENT_ID is not set. See --help."
[[ -n "${CERTIFICATE_ARN:-}" ]] || die "CERTIFICATE_ARN is not set. See --help."

if [[ -z "${ALLOWED_GITHUB_USERS:-}" && -z "${ALLOWED_GITHUB_ORGS:-}" && "${ALLOW_ANY_GITHUB_USER:-}" != "true" ]]; then
  die "no access rule set. Give ALLOWED_GITHUB_USERS and/or ALLOWED_GITHUB_ORGS,
       or set ALLOW_ANY_GITHUB_USER=true to let in every GitHub account."
fi

aws() { command aws --region "$AWS_REGION" "$@"; }
ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)" \
  || die "cannot reach AWS. Run 'aws configure' first."
readonly REGISTRY="${ACCOUNT_ID}.dkr.ecr.${AWS_REGION}.amazonaws.com"
readonly IMAGE="${REGISTRY}/${ECR_REPO}:latest"

# --------------------------------------------------------------------------
# Network
# --------------------------------------------------------------------------
step "Finding the network"
if [[ -z "${VPC_ID:-}" ]]; then
  VPC_ID="$(aws ec2 describe-vpcs --filters Name=isDefault,Values=true \
    --query 'Vpcs[0].VpcId' --output text)"
  [[ "$VPC_ID" != "None" ]] || die "no default VPC in $AWS_REGION; set VPC_ID and SUBNET_IDS"
fi
if [[ -z "${SUBNET_IDS:-}" ]]; then
  # An ALB needs at least two subnets in different availability zones.
  SUBNET_IDS="$(aws ec2 describe-subnets --filters "Name=vpc-id,Values=$VPC_ID" \
    --query 'Subnets[].SubnetId' --output text | tr '\t' ',')"
fi
info "vpc $VPC_ID"
info "subnets $SUBNET_IDS"

# --------------------------------------------------------------------------
# Secrets
# --------------------------------------------------------------------------
secret_arn() {
  aws secretsmanager describe-secret --secret-id "$1" --query ARN --output text 2>/dev/null
}

put_secret() {
  local name="$1" value="$2"
  if secret_arn "$name" >/dev/null 2>&1; then
    aws secretsmanager put-secret-value --secret-id "$name" --secret-string "$value" >/dev/null
  else
    aws secretsmanager create-secret --name "$name" --secret-string "$value" >/dev/null
  fi
}

read_secret_into() {
  local name="$1" label="$2" url="$3"
  [[ -t 0 ]] || die "no terminal to prompt on. Run this interactively to set the $label secret."
  printf '\n    Paste the %s (input hidden).\n' "$label"
  if [[ -n "$url" ]]; then printf '    Find it at %s\n' "$url"; fi
  printf '    Value: '
  read -rs VALUE
  printf '\n'
  [[ -n "$VALUE" ]] || die "nothing entered"
  put_secret "$name" "$VALUE"
  unset VALUE
  info "$name  stored"
}

step "Preparing secrets"
if $ROTATE_SESSION_SECRET || ! secret_arn "$SECRET_SESSION" >/dev/null 2>&1; then
  if $ROTATE_SESSION_SECRET; then
    warn "rotating the session key — everyone is signed out and every robot needs a new token"
  fi
  put_secret "$SECRET_SESSION" "$(openssl rand -base64 48)"
  info "$SECRET_SESSION  generated"
else
  info "$SECRET_SESSION  already set"
fi

if $SET_CLIENT_SECRET || ! secret_arn "$SECRET_CLIENT" >/dev/null 2>&1; then
  read_secret_into "$SECRET_CLIENT" "GitHub OAuth App client secret" \
    "https://github.com/settings/developers"
else
  info "$SECRET_CLIENT  already set"
fi

if $SET_OPENAI_KEY; then
  read_secret_into "$SECRET_OPENAI" "OpenAI API key" "https://platform.openai.com/api-keys"
fi
if $SET_GEMINI_KEY; then
  read_secret_into "$SECRET_GEMINI" "Gemini API key" "https://aistudio.google.com/apikey"
fi

# --------------------------------------------------------------------------
# Image
# --------------------------------------------------------------------------
step "Building and pushing the image"
aws ecr describe-repositories --repository-names "$ECR_REPO" >/dev/null 2>&1 \
  || aws ecr create-repository --repository-name "$ECR_REPO" >/dev/null
aws ecr get-login-password | docker login --username AWS --password-stdin "$REGISTRY" >/dev/null

# linux/amd64 explicitly: Fargate will not run an arm64 image on an X86_64 task,
# and building on an Apple machine produces arm64 by default.
docker build --platform linux/amd64 -t "$IMAGE" .
docker push "$IMAGE" >/dev/null
info "pushed $IMAGE"

# --------------------------------------------------------------------------
# Roles
# --------------------------------------------------------------------------
step "Ensuring IAM roles"
EXEC_ROLE="${SERVICE}-execution"
if ! command aws iam get-role --role-name "$EXEC_ROLE" >/dev/null 2>&1; then
  command aws iam create-role --role-name "$EXEC_ROLE" \
    --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]}' >/dev/null
  command aws iam attach-role-policy --role-name "$EXEC_ROLE" \
    --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy >/dev/null
  info "created $EXEC_ROLE"
else
  info "$EXEC_ROLE already exists"
fi

# Reading the secrets is granted per secret rather than across the account, so
# this task cannot read anything else stored in Secrets Manager.
SECRET_ARNS=()
for name in "$SECRET_SESSION" "$SECRET_CLIENT" "$SECRET_OPENAI" "$SECRET_GEMINI"; do
  if arn="$(secret_arn "$name" 2>/dev/null)"; then SECRET_ARNS+=("\"$arn\""); fi
done
POLICY="{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Action\":[\"secretsmanager:GetSecretValue\"],\"Resource\":[$(IFS=,; echo "${SECRET_ARNS[*]}")]}]}"
command aws iam put-role-policy --role-name "$EXEC_ROLE" \
  --policy-name marvin-secrets --policy-document "$POLICY" >/dev/null
info "secret access granted"

# --------------------------------------------------------------------------
# Load balancer
# --------------------------------------------------------------------------
step "Ensuring the load balancer"
alb_sg() {
  aws ec2 describe-security-groups --filters "Name=vpc-id,Values=$VPC_ID" \
    "Name=group-name,Values=$1" --query 'SecurityGroups[0].GroupId' --output text 2>/dev/null
}

ensure_sg() {
  local name="$1" desc="$2" id
  id="$(alb_sg "$name")"
  if [[ "$id" == "None" || -z "$id" ]]; then
    id="$(aws ec2 create-security-group --group-name "$name" --description "$desc" \
      --vpc-id "$VPC_ID" --query GroupId --output text)"
  fi
  echo "$id"
}

ALB_SG="$(ensure_sg "${SERVICE}-alb" "Marvin load balancer")"
TASK_SG="$(ensure_sg "${SERVICE}-task" "Marvin task")"

# Idempotent: authorize-security-group-ingress fails when the rule is already
# there, which is not an error here.
aws ec2 authorize-security-group-ingress --group-id "$ALB_SG" \
  --protocol tcp --port 443 --cidr 0.0.0.0/0 >/dev/null 2>&1 || true
aws ec2 authorize-security-group-ingress --group-id "$TASK_SG" \
  --protocol tcp --port 8080 --source-group "$ALB_SG" >/dev/null 2>&1 || true

ALB_ARN="$(aws elbv2 describe-load-balancers --names "$SERVICE" \
  --query 'LoadBalancers[0].LoadBalancerArn' --output text 2>/dev/null || true)"
if [[ -z "$ALB_ARN" || "$ALB_ARN" == "None" ]]; then
  ALB_ARN="$(aws elbv2 create-load-balancer --name "$SERVICE" --type application \
    --scheme internet-facing --security-groups "$ALB_SG" \
    --subnets ${SUBNET_IDS//,/ } \
    --query 'LoadBalancers[0].LoadBalancerArn' --output text)"
  info "created the load balancer"
fi

TG_ARN="$(aws elbv2 describe-target-groups --names "$SERVICE" \
  --query 'TargetGroups[0].TargetGroupArn' --output text 2>/dev/null || true)"
if [[ -z "$TG_ARN" || "$TG_ARN" == "None" ]]; then
  TG_ARN="$(aws elbv2 create-target-group --name "$SERVICE" --protocol HTTP --port 8080 \
    --vpc-id "$VPC_ID" --target-type ip \
    --health-check-path /healthz \
    --query 'TargetGroups[0].TargetGroupArn' --output text)"
  info "created the target group"
fi

# An idle robot sends nothing between conversations, and the ALB's default
# sixty-second idle timeout would close the link underneath it. Four hours is
# comfortably longer than any silence and shorter than a stale connection is
# worth keeping.
aws elbv2 modify-load-balancer-attributes --load-balancer-arn "$ALB_ARN" \
  --attributes Key=idle_timeout.timeout_seconds,Value=14400 >/dev/null

if ! aws elbv2 describe-listeners --load-balancer-arn "$ALB_ARN" \
     --query 'Listeners[?Port==`443`]' --output text | grep -q .; then
  aws elbv2 create-listener --load-balancer-arn "$ALB_ARN" \
    --protocol HTTPS --port 443 --certificates "CertificateArn=$CERTIFICATE_ARN" \
    --default-actions "Type=forward,TargetGroupArn=$TG_ARN" >/dev/null
  info "created the HTTPS listener"
fi

ALB_DNS="$(aws elbv2 describe-load-balancers --load-balancer-arns "$ALB_ARN" \
  --query 'LoadBalancers[0].DNSName' --output text)"

# --------------------------------------------------------------------------
# Task definition and service
# --------------------------------------------------------------------------
step "Registering the task definition"
ENVS=()

# A function rather than `[[ -n x ]] && ENVS+=(...)`: that idiom returns
# non-zero when the value is empty, and under `set -e` the next person to move
# one of these lines to the end of the list gets a script that exits silently.
add_env() {
  [[ -n "$2" ]] || return 0
  ENVS+=("$(printf '{"name":"%s","value":"%s"}' "$1" "$2")")
}

add_env GITHUB_CLIENT_ID "$GITHUB_CLIENT_ID"
add_env SESSION_TTL_HOURS "$SESSION_TTL_HOURS"
add_env ALLOWED_GITHUB_USERS "${ALLOWED_GITHUB_USERS:-}"
add_env ALLOWED_GITHUB_ORGS "${ALLOWED_GITHUB_ORGS:-}"
if [[ "${ALLOW_ANY_GITHUB_USER:-}" == "true" ]]; then add_env ALLOW_ANY_GITHUB_USER true; fi
add_env BASE_URL "${BASE_URL:-}"
add_env AI_PROVIDER "${AI_PROVIDER:-}"
add_env AI_MODEL "${AI_MODEL:-}"
add_env AI_VOICE "${AI_VOICE:-}"
add_env DEVICE_TOKEN_TTL_DAYS "${DEVICE_TOKEN_TTL_DAYS:-}"

SECRETS=()
SECRETS+=("{\"name\":\"SESSION_SECRET\",\"valueFrom\":\"$(secret_arn "$SECRET_SESSION")\"}")
SECRETS+=("{\"name\":\"GITHUB_CLIENT_SECRET\",\"valueFrom\":\"$(secret_arn "$SECRET_CLIENT")\"}")
if arn="$(secret_arn "$SECRET_OPENAI" 2>/dev/null)"; then
  SECRETS+=("{\"name\":\"OPENAI_API_KEY\",\"valueFrom\":\"$arn\"}")
fi
if arn="$(secret_arn "$SECRET_GEMINI" 2>/dev/null)"; then
  SECRETS+=("{\"name\":\"GEMINI_API_KEY\",\"valueFrom\":\"$arn\"}")
fi

aws logs create-log-group --log-group-name "/ecs/$SERVICE" >/dev/null 2>&1 || true

TASK_JSON="$(cat <<JSON
{
  "family": "$SERVICE",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "512",
  "memory": "1024",
  "executionRoleArn": "arn:aws:iam::${ACCOUNT_ID}:role/${EXEC_ROLE}",
  "containerDefinitions": [{
    "name": "$SERVICE",
    "image": "$IMAGE",
    "essential": true,
    "portMappings": [{"containerPort": 8080, "protocol": "tcp"}],
    "environment": [$(IFS=,; echo "${ENVS[*]}")],
    "secrets": [$(IFS=,; echo "${SECRETS[*]}")],
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/ecs/$SERVICE",
        "awslogs-region": "$AWS_REGION",
        "awslogs-stream-prefix": "ecs"
      }
    }
  }]
}
JSON
)"

TASK_ARN="$(aws ecs register-task-definition --cli-input-json "$TASK_JSON" \
  --query 'taskDefinition.taskDefinitionArn' --output text)"
info "registered $TASK_ARN"

step "Deploying the service"
aws ecs describe-clusters --clusters "$CLUSTER" --query 'clusters[0].status' --output text 2>/dev/null \
  | grep -q ACTIVE || aws ecs create-cluster --cluster-name "$CLUSTER" >/dev/null

if ! $ASSUME_YES; then
  printf '\n    Deploying to %s/%s in %s\n' "$CLUSTER" "$SERVICE" "$AWS_REGION"
  printf '    Continue? [y/N] '
  read -r reply
  [[ "$reply" == [yY]* ]] || die "cancelled"
fi

EXISTING="$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" \
  --query 'services[0].status' --output text 2>/dev/null || true)"

# One task, matching the Google deployment and for the same reason: the live
# device list and the provider chosen in the controller are per-instance memory.
if [[ "$EXISTING" == "ACTIVE" ]]; then
  aws ecs update-service --cluster "$CLUSTER" --service "$SERVICE" \
    --task-definition "$TASK_ARN" --force-new-deployment >/dev/null
  info "updated the existing service"
else
  aws ecs create-service --cluster "$CLUSTER" --service-name "$SERVICE" \
    --task-definition "$TASK_ARN" --desired-count 1 --launch-type FARGATE \
    --network-configuration "awsvpcConfiguration={subnets=[${SUBNET_IDS}],securityGroups=[${TASK_SG}],assignPublicIp=ENABLED}" \
    --load-balancers "targetGroupArn=${TG_ARN},containerName=${SERVICE},containerPort=8080" \
    --health-check-grace-period-seconds 60 >/dev/null
  info "created the service"
fi

# --------------------------------------------------------------------------
# Report
# --------------------------------------------------------------------------
step "Deployed"
printf '    Load balancer:  %s\n' "$ALB_DNS"
printf '    Point your domain at that name, and make sure it matches the\n'
printf '    certificate — the robot verifies TLS and will refuse a mismatch.\n\n'
printf '    GitHub OAuth callback URL: %s/auth/callback\n' "${BASE_URL:-https://your-domain}"
printf '    Controller:                %s/app/\n' "${BASE_URL:-https://your-domain}"
printf '    Robot endpoint:            %s/v1/device\n' \
  "$(printf '%s' "${BASE_URL:-https://your-domain}" | sed 's|^https|wss|')"
printf '\n    Logs: aws logs tail /ecs/%s --follow --region %s\n' "$SERVICE" "$AWS_REGION"
