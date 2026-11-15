# Deployment

Four images, built from the repository root:

| Image | Dockerfile |
|---|---|
| `ai-content-web` | `src/frontend/Dockerfile` |
| `ai-content-api` | `src/backend/AiContent.Api/Dockerfile` |
| `ai-content-agent-worker` | `src/backend/AiContent.AgentWorker/Dockerfile` |
| `ai-content-video-worker` | `video-engine/Dockerfile` |

Log in once, with your own Docker Hub account. The scripts do not contain a password.

```bash
docker login
./scripts/docker-build-push.sh --username you --tag v1.0.0 --dry-run
./scripts/docker-build-push.sh --username you --tag v1.0.0
```

```powershell
docker login
.\scripts\docker-build-push.ps1 -Username you -Tag v1.0.0 -DryRun
.\scripts\docker-build-push.ps1 -Username you -Tag v1.0.0
```

`--service web` (or `-Service web`) builds one image. `--latest` adds a moving tag; production manifests should still pin the version tag. Nothing is pushed by this repository on its own.

## Compose

`docker compose up --build` starts Postgres, Redis, the video bridge, the API, the worker, and the UI. Media is a shared volume mounted at `/data` in the API, the worker, and the video container, which is what lets the worker read an export path the engine wrote. The bridge token in Compose is a local default, not a production secret.

## Kubernetes

The namespace is `ai-content` and the scripts refuse anything else. They do not install an ingress controller, a storage class, or cert-manager.

Before a real apply:

1. Choose the context, the ingress host, and a storage class that can provide the media volume. The manifest asks for `ReadWriteMany` because the API, the worker, and the video pod all mount it. A cluster without RWX needs a different volume plan; do not switch that silently.
2. Create `ai-content-secrets` from `deploy/k8s/secret.example.yaml`. Required keys: `JWT_SIGNING_KEY`, `AI_API_KEY`, `ENGINE_INTERNAL_TOKEN`, `DATABASE_URL`, `REDIS_URL`.
3. Create `ai-content-tls` for the ingress host, or change the ingress. The sample host `studio.example.com` must be replaced.
4. If the registry is private, create an image pull secret and pass `--pull-secret`.
5. Point `DATABASE_URL` and `REDIS_URL` at Postgres and Redis you operate, with backups. `--with-data-services` applies `deploy/k8s/data` for a single-replica development database. That is not a production database.
6. Confirm the ingress controller namespace is labeled `kubernetes.io/metadata.name=ingress-nginx`, or edit `networkpolicy.yaml`. The video bridge accepts traffic only from the agent worker.

```bash
./scripts/deploy-k8s.sh --tag v1.0.0 --context my-cluster --username you --host studio.example.com --dry-run
./scripts/deploy-k8s.sh --tag v1.0.0 --context my-cluster --username you --host studio.example.com
```

```powershell
.\scripts\deploy-k8s.ps1 -Tag v1.0.0 -Context my-cluster -Username you -HostName studio.example.com -DryRun
.\scripts\deploy-k8s.ps1 -Tag v1.0.0 -Context my-cluster -Username you -HostName studio.example.com
```

Do not run the apply until the context, namespace, host, storage, and secrets have been confirmed. The video deployment stays at one replica because the engine edits one project at a time. The web and API have a small HPA. Probes hit `/health` and `/ready` on the API, `/` on the web and the worker's port 8081, and `/health` on the bridge.

Images are referenced as `__REGISTRY__/ai-content-*:__TAG__` until the script substitutes them. `latest` is not the default.
