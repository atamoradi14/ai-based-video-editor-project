# Development

Requirements: .NET SDK 10, Node.js 22, FFmpeg with libass, and a font that covers Arabic (Noto Naskh Arabic). Postgres and Redis are optional until you run Compose.

```bash
dotnet test src/backend/AiContent.Tests/AiContent.Tests.csproj
dotnet ef migrations add <Name> --project src/backend/AiContent.Infrastructure --startup-project src/backend/AiContent.Api
```

`DOTNET_ROOT` must point at the SDK if `dotnet-ef` cannot find it. Migrations target Postgres. SQLite development uses `EnsureCreated` and does not run those migrations.

The solution is `src/backend/AiContent.sln`. Projects:

- `AiContent.Domain` — entities and statuses
- `AiContent.Application` — planner, change-set checks, ports
- `AiContent.Infrastructure` — EF, storage, engine client, worker loop
- `AiContent.Api` — HTTP, JWT, SignalR
- `AiContent.AgentWorker` — the same worker as its own process, plus a health port on 8081
- `AiContent.Tests`

The UI is `src/frontend` (Next.js, TypeScript, Tailwind, shadcn). Product pages:

- `/` sign in and register
- `/projects` library
- `/projects/new` brief, theme, footage, generate
- `/projects/[id]` player, selection, correction, revisions, cover, export

Jobs move through `Queued`, `Analyzing`, `Generating`, `Rendering`, then `ReadyForReview`. The UI prints the worker's last stage message. It does not invent a percent.

A correction sends `{ instruction, baseRevision, selection }`. `selection.kind` is `project`, `range`, `subtitle`, `audio`, or `cover`. The API rejects a second active job for the same project and rejects a change whose times fall outside the selection.

To refresh the vendored engine, clone the upstream commit you intend to pin, replace `video-engine/ai-edit-studio`, update `UPSTREAM_PIN.json`, and re-run `node --test` plus `dotnet test`. The allowlist test fails if the tool names moved.
