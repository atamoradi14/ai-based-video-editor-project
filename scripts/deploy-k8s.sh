#!/usr/bin/env bash
# Deploy AI Content Studio into the ai-content namespace.
# This script never targets another namespace and never prints secret values.
# Example: ./scripts/deploy-k8s.sh --tag v1.0.0 --context my-cluster
set -euo pipefail

tag=""
context="${K8S_CONTEXT:-}"
username="${DOCKERHUB_USERNAME:-}"
prefix="${DOCKERHUB_IMAGE_PREFIX:-ai-content}"
namespace="ai-content"
host="${K8S_INGRESS_HOST:-studio.example.com}"
dry=0
with_data=0
pull_secret=""

usage() {
  cat <<'EOF'
Usage: deploy-k8s.sh --tag <tag> --context <context> [options]
  --username <name>         Docker Hub user (or DOCKERHUB_USERNAME)
  --prefix <name>           Image prefix (default: ai-content)
  --host <hostname>         Ingress host (or K8S_INGRESS_HOST)
  --pull-secret <name>      Optional imagePullSecret
  --with-data-services      Also apply deploy/k8s/data (dev Postgres and Redis)
  --dry-run                 Render and client-dry-run. Does not change the cluster.
The namespace is always ai-content.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) tag="$2"; shift 2 ;;
    --context) context="$2"; shift 2 ;;
    --username) username="$2"; shift 2 ;;
    --prefix) prefix="$2"; shift 2 ;;
    --host) host="$2"; shift 2 ;;
    --pull-secret) pull_secret="$2"; shift 2 ;;
    --with-data-services) with_data=1; shift ;;
    --dry-run) dry=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

[[ -n "$tag" && -n "$context" && -n "$username" ]] || { echo "Required: --tag, --context, and a Docker Hub username" >&2; exit 1; }
[[ "$namespace" == "ai-content" ]] || { echo "Refusing to touch namespace $namespace" >&2; exit 1; }
command -v kubectl >/dev/null || { echo "kubectl is not installed" >&2; exit 1; }

echo "Target context: $context"
kubectl --context "$context" config current-context
kubectl --context "$context" cluster-info
kubectl --context "$context" auth can-i create namespace
kubectl --context "$context" auth can-i create deployment -n "$namespace"

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -a "$root/deploy/k8s/." "$work/"
find "$work" -type f -name '*.yaml' -print0 | xargs -0 sed -i \
  -e "s|__REGISTRY__/ai-content-|${username}/${prefix}-|g" \
  -e "s|__TAG__|${tag}|g" \
  -e "s|studio.example.com|${host}|g"
if [[ -n "$pull_secret" ]]; then
  find "$work" -name 'workloads.yaml' -print0 | xargs -0 sed -i \
    -e "s|serviceAccountName: ai-content|serviceAccountName: ai-content\n      imagePullSecrets:\n        - { name: ${pull_secret} }|"
fi

echo "Namespace: $namespace"
echo "Images: ${username}/${prefix}-{web,api,agent-worker,video-worker}:${tag}"
echo "Ingress host: $host"
if [[ "$dry" -eq 1 ]]; then
  kubectl --context "$context" apply --dry-run=client -f "$work/namespace.yaml"
  kubectl --context "$context" apply --dry-run=client -k "$work"
  if [[ "$with_data" -eq 1 ]]; then
    kubectl --context "$context" apply --dry-run=client -f "$work/data"
  fi
  echo "Dry run only. The cluster was not changed."
  exit 0
fi

kubectl --context "$context" apply -f "$work/namespace.yaml"
kubectl --context "$context" get namespace "$namespace" >/dev/null
missing=0
for key in JWT_SIGNING_KEY AI_API_KEY ENGINE_INTERNAL_TOKEN DATABASE_URL REDIS_URL; do
  if ! kubectl --context "$context" -n "$namespace" get secret ai-content-secrets -o jsonpath="{.data.${key}}" >/dev/null 2>&1; then
    echo "Secret ai-content-secrets is missing key ${key}. See deploy/k8s/secret.example.yaml" >&2
    missing=1
  fi
done
[[ "$missing" -eq 0 ]] || exit 1
if ! kubectl --context "$context" -n "$namespace" get secret ai-content-tls >/dev/null 2>&1; then
  echo "TLS secret ai-content-tls is not present. Create it before traffic will succeed." >&2
fi

kubectl --context "$context" apply -k "$work"
if [[ "$with_data" -eq 1 ]]; then kubectl --context "$context" apply -f "$work/data"; fi
kubectl --context "$context" -n "$namespace" rollout status deployment/ai-content-api --timeout=180s
kubectl --context "$context" -n "$namespace" rollout status deployment/ai-content-web --timeout=180s
kubectl --context "$context" -n "$namespace" rollout status deployment/ai-content-agent-worker --timeout=180s
kubectl --context "$context" -n "$namespace" rollout status deployment/ai-content-video --timeout=240s
kubectl --context "$context" -n "$namespace" get pods,svc,ingress
echo "If a rollout failed: kubectl --context $context -n $namespace describe pods"
echo "Logs: kubectl --context $context -n $namespace logs deploy/ai-content-agent-worker"
