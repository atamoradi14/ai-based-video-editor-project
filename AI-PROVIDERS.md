# AI providers

Set these in the environment of the API and the agent worker. Do not put the key in a `NEXT_PUBLIC_` variable.

```
AI_PROVIDER=openai
AI_MODEL=gpt-6.1-luna
AI_API_KEY=
AI_BASE_URL=https://api.openai.com/v1
```

`AI_PROVIDER` is `openai`, `azure`, `compatible`, or `fixture`.

- `openai` and `compatible` POST `{AI_BASE_URL}/chat/completions` with a bearer token.
- `azure` POSTs `{AI_BASE_URL}/openai/deployments/{AI_MODEL}/chat/completions?api-version=2024-10-21` with an `api-key` header. `AI_MODEL` is the deployment name.
- `fixture` never opens a network connection. Use it for Compose and CI.

`gpt-6.1-luna` is only an example of a configurable string. If the endpoint says the model does not exist, the job fails with `model_unavailable`. The worker does not retry that against a different provider.

The .NET side uses Microsoft Agent Framework (`ChatClientAgent` / `AsAIAgent`) with one function, `SubmitPlan` or `SubmitChange`. The chat transport is `OpenAiCompatibleChatClient` in `src/backend/AiContent.Infrastructure/Services/AgentServices.cs`, because the framework's OpenAI client does not by itself know this engine. Tool calls are capped by `AI_MAX_TOOL_CALLS`. HTTP 429 and 5xx are retried. Other errors are not.

Image generation is separate and off until you set `AI_IMAGE_PROVIDER`. A failed image edit does not switch providers. Transcription is `local` (engine whisper, or a sidecar file) or `openai` (`AI_TRANSCRIPTION_MODEL` against `{AI_BASE_URL}/audio/transcriptions`). Word-level timing is only claimed when the recognizer actually returned words.

Leave the key empty in development. The UI says when the fixture planner is in use.
