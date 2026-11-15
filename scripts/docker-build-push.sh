#!/usr/bin/env bash
# Build and push the four AI Content Studio images.
# Does not embed credentials. Run `docker login` first.
# Example: ./scripts/docker-build-push.sh --tag v1.0.0
set -euo pipefail

username="${DOCKERHUB_USERNAME:-}"
prefix="${DOCKERHUB_IMAGE_PREFIX:-ai-content}"
tag=""
platforms="linux/amd64"
latest=0
service="all"
dry=0

usage() {
  cat <<'EOF'
Usage: docker-build-push.sh --tag <tag> [options]
  --username <name>     Docker Hub user (or DOCKERHUB_USERNAME)
  --prefix <name>       Image prefix (default: ai-content)
  --tag <tag>           Required image tag, for example v1.0.0 or a git SHA
  --platform <list>     Buildx platforms (default: linux/amd64)
  --latest              Also tag :latest
  --service <name>      all | web | api | agent-worker | video-worker
  --dry-run             Print the buildx commands and exit
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --username) username="$2"; shift 2 ;;
    --prefix) prefix="$2"; shift 2 ;;
    --tag) tag="$2"; shift 2 ;;
    --platform) platforms="$2"; shift 2 ;;
    --latest) latest=1; shift ;;
    --service) service="$2"; shift 2 ;;
    --dry-run) dry=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage; exit 1 ;;
  esac
done

[[ -n "$tag" ]] || { echo "Missing --tag" >&2; exit 1; }
[[ -n "$username" ]] || { echo "Missing --username or DOCKERHUB_USERNAME" >&2; exit 1; }
[[ "$tag" != "latest" ]] || { echo "Refusing to use latest as the only tag" >&2; exit 1; }

case "$service" in
  all|web|api|agent-worker|video-worker) ;;
  *) echo "Unknown service: $service" >&2; exit 1 ;;
esac

command -v docker >/dev/null || { echo "docker is not installed" >&2; exit 1; }
docker buildx version >/dev/null || { echo "docker buildx is not available" >&2; exit 1; }
if [[ "$dry" -eq 0 ]]; then
  docker info >/dev/null || { echo "docker daemon is not reachable" >&2; exit 1; }
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
declare -a names=()
declare -a files=()
add() { names+=("$1"); files+=("$2"); }
if [[ "$service" == "all" || "$service" == "web" ]]; then add "${prefix}-web" src/frontend/Dockerfile; fi
if [[ "$service" == "all" || "$service" == "api" ]]; then add "${prefix}-api" src/backend/AiContent.Api/Dockerfile; fi
if [[ "$service" == "all" || "$service" == "agent-worker" ]]; then add "${prefix}-agent-worker" src/backend/AiContent.AgentWorker/Dockerfile; fi
if [[ "$service" == "all" || "$service" == "video-worker" ]]; then add "${prefix}-video-worker" video-engine/Dockerfile; fi

for i in "${!names[@]}"; do
  image="${username}/${names[$i]}"
  tags=(-t "${image}:${tag}")
  if [[ "$latest" -eq 1 ]]; then tags+=(-t "${image}:latest"); fi
  cmd=(docker buildx build --platform "$platforms" -f "$root/${files[$i]}" "${tags[@]}" "$root")
  if [[ "$dry" -eq 0 ]]; then cmd+=(--push); fi
  printf 'image %s:%s\n' "$image" "$tag"
  printf ' +'; printf ' %q' "${cmd[@]}"; printf '\n'
  if [[ "$dry" -eq 0 ]]; then
    "${cmd[@]}"
    docker buildx imagetools inspect "${image}:${tag}"
  fi
done

if [[ "$dry" -eq 1 ]]; then
  echo "Dry run only. Nothing was built or pushed. docker login is required before a real push."
fi
