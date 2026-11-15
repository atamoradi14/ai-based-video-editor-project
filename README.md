# Lumen — AI Content Studio

A self-hosted studio for Instagram drafts. You upload a raw video, name the subject and theme, and a background job produces a playable vertical cut, a cover, captions, and a caption pack. You can then ask for a change to one time range, or to the cover alone. The cover change does not render the video again.

The API is ASP.NET Core 10. Editing is the vendored AI Edit Studio engine (Node.js and FFmpeg), pinned in `video-engine/UPSTREAM_PIN.json`. The model name is configuration. `gpt-6.1-luna` in the examples is not assumed to exist, and an unknown model is reported instead of being silently replaced.

## Run it on this machine

The development API uses SQLite, runs the worker in-process, and seeds `demo@lumen.studio` / `Demo-Studio-1` when `SEED_DEMO_USER=true`.

Linux or macOS:

```bash
export PATH="$HOME/.dotnet:$PATH"
cd src/backend
dotnet run --project AiContent.Api
```

In another shell, with the engine bridge if you want real cuts:

```bash
cd video-engine
ENGINE_INTERNAL_TOKEN=dev-token BRIDGE_BIND=127.0.0.1 BRIDGE_PORT=32147 \
  GAME_TRAILER_STUDIO_DATA_ROOT=/tmp/lumen-engine node bridge/server.mjs
```

And the UI:

```bash
cd src/frontend
NEXT_PUBLIC_API_URL=http://localhost:8080 npm run dev -- --port 3410 --hostname 0.0.0.0
```

Open `http://localhost:3410`. Sign in with the demo account, create a project, upload an MP4, and generate. With `AI_PROVIDER=fixture` (the default in `appsettings.Development.json`) the editorial plan is deterministic and the video edit is real.

Windows, from the repository root, after installing the .NET 10 SDK, Node 22, and FFmpeg:

```powershell
dotnet run --project src\backend\AiContent.Api
# second terminal
$env:BRIDGE_BIND="127.0.0.1"; $env:BRIDGE_PORT="32147"; $env:GAME_TRAILER_STUDIO_DATA_ROOT="$env:TEMP\lumen-engine"; node video-engine\bridge\server.mjs
# third terminal
cd src\frontend; $env:NEXT_PUBLIC_API_URL="http://localhost:8080"; npm run dev -- --port 3410
```

## Docker Compose

```bash
docker compose up --build
```

The UI is on port 3410 and the API on port 8080. Compose uses Postgres, Redis, the fixture planner, and the authenticated engine bridge. The demo user is seeded. See `DEPLOYMENT.md` before pointing Compose at a live model key.

## Tests

```bash
dotnet test src/backend/AiContent.Tests/AiContent.Tests.csproj
node --test video-engine/ai-edit-studio
node --test video-engine/bridge/bridge.test.mjs
```

The engine test renders a real short MP4. Provider tests never call a paid API.

## More

- `ARCHITECTURE.md` — processes and the security boundary
- `UPSTREAM_INTEGRATION.md` — the real tool map
- `AI-PROVIDERS.md` — how to set a model
- `DEVELOPMENT.md` — local layout
- `ENVIRONMENT.md` — every variable
- `DEPLOYMENT.md` — images and Kubernetes
