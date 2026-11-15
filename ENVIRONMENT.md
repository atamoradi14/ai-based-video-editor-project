# Environment

Names match `.env.example`. The API binds them in `StudioOptions.From`. Empty `DATABASE_URL` selects SQLite. A URL that starts with `postgres://` or `postgresql://` is converted to an Npgsql connection string; a `Host=` string is used as-is.

| Name | Purpose |
|---|---|
| `AI_PROVIDER` | `fixture`, `openai`, `azure`, or `compatible` |
| `AI_MODEL` | Model or Azure deployment. Not validated at startup |
| `AI_API_KEY` | Bearer token, or Azure `api-key`. Backend only |
| `AI_BASE_URL` | API root, no trailing path beyond `/v1` |
| `AI_TIMEOUT_SECONDS`, `AI_MAX_OUTPUT_TOKENS`, `AI_MAX_TOOL_CALLS`, `AI_MAX_RETRIES` | Call budget |
| `AI_IMAGE_PROVIDER`, `AI_IMAGE_MODEL`, `AI_IMAGE_API_KEY`, `AI_IMAGE_BASE_URL` | Optional cover restyle |
| `AI_TRANSCRIPTION_PROVIDER`, `AI_TRANSCRIPTION_MODEL` | `local` or `openai` |
| `DATABASE_URL`, `DATABASE_PROVIDER`, `SQLITE_PATH` | Postgres, or SQLite for development and tests |
| `REDIS_URL` | `host:6379`. A `redis://` prefix is stripped. Progress fan-out; the queue of record is Postgres |
| `STORAGE_PROVIDER` | `local`, `s3`, or `azure` |
| `STORAGE_PATH` | Local root. Keys never leave this directory |
| `AZURE_STORAGE_CONNECTION_STRING`, `AZURE_STORAGE_CONTAINER` | Azure Blob |
| `S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_REGION` | S3-compatible |
| `ENGINE_BASE_URL` | Bridge or loopback engine, for example `http://127.0.0.1:32147` |
| `ENGINE_INTERNAL_TOKEN` | Required when the bridge binds a non-loopback address |
| `ENGINE_DATA_ROOT` | Same directory the engine was given as `GAME_TRAILER_STUDIO_DATA_ROOT` |
| `JWT_SIGNING_KEY` | Required outside Development. Development writes `STORAGE_PATH/dev-jwt.key` when this is empty |
| `JWT_ISSUER`, `JWT_HOURS` | Token issuer and lifetime |
| `SEED_DEMO_USER`, `DEMO_EMAIL`, `DEMO_PASSWORD` | Development seed only |
| `PUBLIC_API_URL` | Prefix for local signed download URLs |
| `WEB_ORIGIN` | Comma-separated CORS origins |
| `RUN_WORKER_IN_API` | `true` runs the worker inside the API process |
| `UPLOAD_MAX_BYTES` | Default 512 MiB |
| `DOCKERHUB_USERNAME`, `DOCKERHUB_IMAGE_PREFIX` | Build script |
| `K8S_CONTEXT`, `K8S_INGRESS_HOST`, `K8S_TLS_SECRET` | Deploy script. The namespace is fixed to `ai-content` |

`NEXT_PUBLIC_API_URL` is the only value the browser sees. It is an origin, not a secret.
