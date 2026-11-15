# Upstream integration

| | |
|---|---|
| Repository | https://github.com/laaielichter/Ai-edit-studio |
| Commit | `46e0250f990bdc6fe74d8e1eca10dbf82bf17a59` |
| Version | 0.12.0 |
| License | MIT, Copyright (c) 2026 laaielichter |
| Vendored at | `video-engine/ai-edit-studio` |
| Tools | 59, exported by `src/agent-tools.mjs` |

The pin file is `video-engine/UPSTREAM_PIN.json`. The data-root variable the server actually reads is `GAME_TRAILER_STUDIO_DATA_ROOT`. Some docs also mention `AI_EDIT_STUDIO_DATA_ROOT`; the plugin maps that name onto the game-trailer variable. This app sets the game-trailer variable.

`node --test` in the vendored tree: 251 passed, 0 failed (about 26 seconds on this machine).

A direct engine smoke (8 second test pattern, `studio_create` with `options.duration` 6, `studio_auto_edit` platform `reels`, `reframe` `9:16`, `preview` true) produced a 720×1280 H.264/AAC preview. `options.duration` below 5 is rejected by the engine.

## Map

| Need | Implementation | Evidence |
|---|---|---|
| Create a project from an absolute file | `studio_create` | Engine pipeline test and the smoke above |
| Probe | ffprobe in `FfprobeMediaProbe`, plus the engine's own analysis during auto-edit | Pipeline test stores duration and codecs |
| Transcribe | `studio_transcribe` only when a local model exists. Otherwise a `.words.json` sidecar or `audio/transcriptions` when that provider is configured | Missing model returns a warning, not invented text. `ASR_UNAVAILABLE` is the engine's own error |
| Editorial plan | Our planner, not the engine Director | Fixture test and the agent-framework unit test |
| Rough cut, reframe, preview | `studio_auto_edit` then `studio_wait` | Pipeline test asserts H.264, AAC, and height greater than width |
| Brand theme | `studio_brand` save/apply, colors as 7-character strings | Called from `GenerationPipeline` |
| Timeline and review | `studio_timeline`, `studio_review` | Stored on the revision. Review is the engine's heuristic score |
| Subtitles | Imported words via `studio_transcript` action `import`, export via `studio_export_timeline` format `srt` | Export test requires a caption asset |
| Selective edit | `studio_edit` dry-run, then apply, then `studio_render_range` or `studio_render` | Pipeline test: range correction creates a new video asset |
| Undo | `studio_restore` when the engine id is known, and our revision rows | `RestoreRevisionAsync` |
| Cover | Our FFmpeg/libass composer. Not `studio_publish_pack` | Cover correction keeps the same video asset id and hash |
| Publish text | `studio_publish_pack` plus the planner's caption and hashtags | Stored on the project. Nothing is posted |
| Quality | `studio_qc`, failures become warnings | Called after the preview is stored |
| Persian burn-in | libass with Noto Naskh Arabic | `LibassRendersPersianAndMixedText` compares a frame with and without the subtitle |

The C# allowlist is checked against `agentTools` and `OPERATIONS` by a unit test, so a vendored update that adds or renames a tool fails the build until the list is updated on purpose.

`studio_publish_pack` writes local thumbnails and a manifest. It is not a generative image editor. Image edits, when `AI_IMAGE_PROVIDER` is set, go to that provider's `/images/edits` endpoint and a failure keeps the template.
