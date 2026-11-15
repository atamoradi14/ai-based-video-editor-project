# Architecture

```
Browser (Next.js)
    |  JWT, REST, polling
    v
ASP.NET Core API  ---- SignalR ---- Redis pub/sub (optional)
    |  enqueue
    v
Postgres jobs  <---- claim / heartbeat ----  .NET agent worker
    |                                          |
    |                                          | allowlisted HTTP tools
    |                                          v
    |                               video bridge :32147 (bearer token)
    |                                          |
    |                                          v
    |                               AI Edit Studio on 127.0.0.1:32146
    |                                          |
    +-------- shared /data volume -------------+
                     FFmpeg, fonts, exports
```

The API does not render inside a request. It inserts a `GenerationJob` and returns 202. The worker claims the job with a conditional update so two workers cannot both own it, heartbeats the lease, and runs the pipeline. Redis publishes progress when it is configured. The UI also polls. A restart picks up a job whose lease expired, until the attempt budget is spent.

The model is not given the studio tools and is not given a shell. It must call `submit_editorial_plan` or `submit_change_set`. The worker checks the change against the selection and then calls an allowlisted engine tool. `AI_PROVIDER=fixture` uses a deterministic planner and does not call a network model. A live provider that returns 404 or `model_not_found` fails the job. It does not fall over to another provider.

The engine process refuses any bind that is not loopback. The bridge is the only listener on a shared network, and it refuses to listen off loopback unless `ENGINE_INTERNAL_TOKEN` is set. It forwards only tool names exported by the vendored engine.

Covers are drawn with FFmpeg and libass from a source frame. A configured image-edit endpoint can restyle that frame. If it fails, the template cover is kept and a warning is stored. Regenerating a cover does not call `studio_render`.

## Security boundary

- Passwords are hashed with ASP.NET Identity's password hasher. Project queries filter on the caller.
- Download URLs for local storage are HMAC-signed and expire. Object storage uses its own signed URL.
- Upload names are not used as paths. Magic bytes are checked for video and images.
- Engine file copies must sit under `ENGINE_DATA_ROOT`.
- FFmpeg is started with an argument list, not a shell.
- Transcript text is data passed to the planner, not a command.
- Secrets are read from the environment or a Kubernetes secret. They are not `NEXT_PUBLIC_` values.

## What is deliberately limited

- Upstream music ducking is a project-wide option in version 0.12.0. A range request records a marker and lowers the music gain for the whole cut. It is not sample-accurate to the selection.
- Local speech recognition needs a whisper.cpp model the engine can see. This environment's FFmpeg has no whisper filter. The pipeline imports a sidecar transcript when one is uploaded and otherwise records a warning. It does not invent words.
- The engine analyzes one project at a time. Run a single video pod.
- Browser playback is the real MP4 the worker stored. The engine's own web UI is not exposed.
