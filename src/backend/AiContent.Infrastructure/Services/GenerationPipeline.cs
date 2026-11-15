using System.Globalization;
using System.Text;
using System.Text.Json;
using AiContent.Application;
using AiContent.Contracts;
using AiContent.Domain;
using AiContent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiContent.Infrastructure.Services;

public sealed class GenerationPipeline(
    AppDbContext db,
    StudioOptions options,
    IAiEditStudioClient engine,
    IAgentOrchestrator agent,
    ITranscriptionService transcription,
    ICoverComposer covers,
    IImageGenerationService images,
    IMediaProbe probe,
    IStorageService storage,
    IProgressPublisher progress,
    IJobQueue jobs,
    ILogger<GenerationPipeline> logger) : IGenerationPipeline
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid jobId, string workerId, CancellationToken ct)
    {
        var job = await db.Jobs.Include(j => j.Project)!.ThenInclude(p => p!.Brief)
            .Include(j => j.Project)!.ThenInclude(p => p!.Assets)
            .Include(j => j.Project)!.ThenInclude(p => p!.Transcripts)
            .FirstAsync(j => j.Id == jobId, ct);
        var project = job.Project ?? throw new StudioException("project_missing", "Job has no project.");
        using var beat = new Timer(_ => _ = jobs.HeartbeatAsync(jobId, workerId, CancellationToken.None), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        try
        {
            if (job.CancelRequested) { await Cancel(project, job, ct); return; }
            switch (job.Kind)
            {
                case JobKind.Generate: await Generate(project, job, ct); break;
                case JobKind.Correct: await Correct(project, job, ct); break;
                case JobKind.Cover: await Correct(project, job, ct); break;
                case JobKind.Export: await Export(project, job, ct); break;
                default: throw new StudioException("unknown_job", "Unknown job kind.");
            }
            await jobs.CompleteAsync(job.Id, JobStatus.Succeeded, null, ct);
        }
        catch (OperationCanceledException)
        {
            await Cancel(project, job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "job {JobId} failed", job.Id);
            var transient = ex is HttpRequestException || ex is StudioException { Code: "engine_busy" or "engine_timeout" };
            if (transient && job.Attempt < job.MaxAttempts)
            {
                await jobs.RequeueAsync(job.Id, ex.Message, CancellationToken.None);
                return;
            }
            project.Status = ProjectStatus.Failed;
            project.StatusDetail = ex.Message;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await progress.PublishAsync(project.OwnerId, project.Id, job.Id, "failed", ex.Message, CancellationToken.None);
            await jobs.CompleteAsync(job.Id, JobStatus.Failed, ex.Message, CancellationToken.None);
        }
    }

    private async Task Generate(Project project, GenerationJob job, CancellationToken ct)
    {
        var video = project.Assets.Where(a => a.Kind is AssetKind.SourceVideo or AssetKind.AdditionalClip).OrderBy(a => a.CreatedAt).FirstOrDefault()
            ?? throw new StudioException("video_required", "Upload a video before generating.");
        var path = await storage.MaterializeAsync(video.StorageKey, ct);
        await Stage(project, job, "probe", "Reading duration, codecs and frame size.", ct);
        var probed = await probe.ProbeAsync(path, ct);
        video.DurationSeconds = probed.Duration;
        video.Width = probed.Width;
        video.Height = probed.Height;
        video.MetadataJson = probed.RawJson;
        await db.SaveChangesAsync(ct);

        var brief = project.Brief ?? throw new StudioException("brief_missing", "The project brief is missing.");
        await Stage(project, job, "plan", "Writing the editorial plan.", ct);
        TranscriptDraft? draft = null;
        var sidecar = project.Assets.FirstOrDefault(a => a.Kind == AssetKind.TranscriptFile);
        var sidecarPath = sidecar is null ? null : await storage.MaterializeAsync(sidecar.StorageKey, ct);
        if (project.EngineProjectId is null)
        {
            var created = await engine.CallAsync("studio_create", new
            {
                name = Clip(project.Title, 100),
                inputs = new[] { path },
                options = new
                {
                    title = Clip(project.Title, 100),
                    brief = Clip(brief.Goal, 2000),
                    label = "",
                    endTitle = "",
                    duration = ClampDuration(brief),
                    masterFormat = "hd",
                    socialSize = "720",
                    captionStyle = Theme(project, "captionStyle", "bold"),
                    captionPosition = Theme(project, "captionPosition", "low"),
                    captionAnimation = Theme(project, "captionAnimation", "highlight"),
                    look = Theme(project, "look", "cinema"),
                    duckMusic = true,
                },
            }, ct);
            var createdProject = AiEditStudioClient.ProjectOf(created);
            project.EngineProjectId = AiEditStudioClient.Text(createdProject, "id") ?? throw new StudioException("engine_create", "studio_create did not return a project id.");
            project.EngineRevision = AiEditStudioClient.IntAt(createdProject, "revision");
            await db.SaveChangesAsync(ct);
        }

        await Stage(project, job, "transcribe", "Looking for speech. Captions are not invented when recognition is unavailable.", ct);
        draft = await transcription.TranscribeAsync(new TranscriptionRequest(path, sidecarPath, brief.Language, project.EngineProjectId), ct);
        if (!string.IsNullOrWhiteSpace(draft.Warning)) Warn(project, draft.Warning);
        await SaveTranscript(project, draft, ct);
        if (draft.Words.Count > 0)
        {
            try
            {
                await engine.CallAsync("studio_transcript", new
                {
                    projectId = project.EngineProjectId,
                    action = "import",
                    words = draft.Words.Select(w => new { start = w.Start, end = w.End, text = w.Text }),
                    language = brief.Language,
                    captions = false,
                }, ct);
            }
            catch (StudioException ex)
            {
                Warn(project, "Transcript import: " + ex.Message);
            }
        }

        var plan = await agent.PlanAsync(new PlanRequest(project.Title, brief.Subject, brief.Goal, brief.Audience, brief.Tone, brief.Language, brief.Platform, brief.TargetDurationSeconds, brief.CallToAction, Excerpt(draft)), ct);
        await Stage(project, job, "plan", plan.Rationale, ct);
        var brandName = await SaveEngineBrand(project, plan, ct);

        await Stage(project, job, "assemble", "AI Edit Studio is cutting, reframing and previewing.", ct);
        await engine.CallAsync("studio_auto_edit", new
        {
            projectId = project.EngineProjectId,
            platform = EnginePlatform(brief.Platform),
            duration = ClampDuration(brief),
            pace = "balanced",
            keep = "highlights",
            hookText = Clip(plan.Hook, 120),
            title = Clip(plan.Title, 100),
            cta = Clip(brief.CallToAction ?? "", 100),
            brief = Clip(plan.Message, 2000),
            transitions = true,
            reframe = EngineAspect(brief.Platform),
            preview = true,
            queue = true,
            brand = brandName,
            author = "aicontent",
        }, ct);
        var ready = await engine.WaitUntilIdleAsync(project.EngineProjectId!, TimeSpan.FromMinutes(8), ct);
        if (AiEditStudioClient.Text(ready, "status") == "failed")
            throw new StudioException("auto_edit_failed", AiEditStudioClient.Text(ready, "message") ?? "Auto-edit failed.");
        project.EngineRevision = AiEditStudioClient.IntAt(ready, "revision");

        await Stage(project, job, "render", "Collecting the playable preview.", ct);
        var videoKey = await engine.CopyExportAsync(ready, storage, ct);
        if (videoKey is null)
        {
            await engine.CallAsync("studio_render", new
            {
                projectId = project.EngineProjectId,
                revision = project.EngineRevision,
                preview = true,
                previewFormat = brief.Platform == "post" ? "portrait" : "vertical",
                queue = true,
            }, ct);
            ready = await engine.WaitUntilIdleAsync(project.EngineProjectId!, TimeSpan.FromMinutes(8), ct);
            videoKey = await engine.CopyExportAsync(ready, storage, ct);
        }
        if (videoKey is null) throw new StudioException("render_missing", "The engine finished without a playable MP4.");
        var file = await storage.MaterializeAsync(videoKey, ct);
        var rendered = await probe.ProbeAsync(file, ct);
        if (rendered.VideoCodec is not "h264" || rendered.AudioCodec is null)
            Warn(project, $"Preview codecs are video={rendered.VideoCodec ?? "none"}, audio={rendered.AudioCodec ?? "none"}.");
        var videoAsset = AddAsset(project, AssetKind.DerivedVideo, "preview.mp4", "video/mp4", videoKey, new FileInfo(file).Length, rendered);
        project.CurrentVideoAssetId = videoAsset.Id;

        await Stage(project, job, "cover", "Composing a cover from a source frame. This does not change the timeline.", ct);
        await MakeCover(project, path, rendered.Duration ?? probed.Duration ?? 1, plan.CoverHeadline, plan.Caption, job, ct);

        string? pack = null;
        try
        {
            var published = await engine.CallAsync("studio_publish_pack", new { projectId = project.EngineProjectId, title = Clip(plan.Title, 80), platforms = new[] { "reels" }, thumbnails = 2 }, ct);
            pack = published.GetRawText();
        }
        catch (StudioException ex) { Warn(project, "Publish pack: " + ex.Message); }
        project.PublishPackJson = JsonSerializer.Serialize(new { plan.Caption, plan.Hashtags, plan.Title, engine = pack });

        try
        {
            var timeline = await engine.CallAsync("studio_timeline", new { projectId = project.EngineProjectId }, ct);
            var review = await engine.CallAsync("studio_review", new { projectId = project.EngineProjectId }, ct);
            AddRevision(project, "Generated draft", timeline.GetRawText(), review.GetRawText(), videoAsset.Id, true, true, "engine");
        }
        catch (StudioException ex)
        {
            Warn(project, "Timeline read: " + ex.Message);
            AddRevision(project, "Generated draft", "{}", null, videoAsset.Id, true, true, "engine");
        }
        try { await engine.CallAsync("studio_qc", new { projectId = project.EngineProjectId }, ct); }
        catch (StudioException ex) { Warn(project, "Quality check: " + ex.Message); }

        project.Status = ProjectStatus.ReadyForReview;
        project.StatusDetail = "Draft is ready to review.";
        project.UpdatedAt = DateTimeOffset.UtcNow;
        project.WarningsJson = JsonSerializer.Serialize(Warnings(project));
        await db.SaveChangesAsync(ct);
        await Stage(project, job, "done", "Draft is ready to review.", ct);
    }

    private async Task Correct(Project project, GenerationJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CorrectionRequest>(job.PayloadJson, Json) ?? throw new StudioException("invalid_job", "Correction payload is missing.");
        if (payload.BaseRevision != project.CurrentRevisionNumber)
            throw new StudioException("revision_conflict", "The project changed before this correction started.", 409);
        var transcript = project.Transcripts.OrderByDescending(t => t.CreatedAt).FirstOrDefault();
        var timeline = await db.Revisions.Where(r => r.ProjectId == project.Id).OrderByDescending(r => r.Number).Select(r => r.TimelineJson).FirstOrDefaultAsync(ct) ?? "{}";
        await Stage(project, job, "plan", "Proposing a typed change for the selection.", ct);
        var change = await agent.ProposeAsync(new CorrectionPrompt(payload.Instruction, payload.Selection, timeline, transcript?.Text ?? "", project.CurrentRevisionNumber), ct);
        ChangeSetValidator.Validate(change, payload.Selection);
        db.Messages.Add(new ConversationMessage { Id = Guid.NewGuid(), ProjectId = project.Id, Role = MessageRole.User, Content = payload.Instruction, SelectionJson = JsonSerializer.Serialize(payload.Selection, Json), CreatedAt = DateTimeOffset.UtcNow });
        db.Messages.Add(new ConversationMessage { Id = Guid.NewGuid(), ProjectId = project.Id, Role = MessageRole.Assistant, Content = change.Summary, ChangeSetJson = JsonSerializer.Serialize(change, Json), CreatedAt = DateTimeOffset.UtcNow });

        if (!change.TouchesVideo)
        {
            var source = project.Assets.First(a => a.Kind == AssetKind.SourceVideo);
            var path = await storage.MaterializeAsync(source.StorageKey, ct);
            await Stage(project, job, "cover", "Regenerating the cover only. The video file is left untouched.", ct);
            await MakeCover(project, path, source.DurationSeconds ?? 1, change.CoverHeadline ?? project.Title, change.CoverPrompt, job, ct);
            AddRevision(project, change.Summary, timeline, JsonSerializer.Serialize(change, Json), project.CurrentVideoAssetId, false, true, "agent");
            project.Status = ProjectStatus.ReadyForReview;
            project.StatusDetail = "Cover updated. The video was not rendered again.";
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await Stage(project, job, "done", project.StatusDetail, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(project.EngineProjectId))
            throw new StudioException("engine_project_missing", "Generate a draft before requesting a timeline correction.");
        var ops = change.Operations.Select(op => Operation(op, change.MusicGain)).ToArray();
        await Stage(project, job, "assemble", "Checking the edit against the current revision.", ct);
        await engine.CallAsync("studio_edit", new { projectId = project.EngineProjectId, revision = project.EngineRevision, dryRun = true, operations = ops, summary = Clip(change.Summary, 200), author = "aicontent" }, ct);
        var applied = await engine.CallAsync("studio_edit", new { projectId = project.EngineProjectId, revision = project.EngineRevision, operations = ops, summary = Clip(change.Summary, 200), author = "aicontent" }, ct);
        var edited = AiEditStudioClient.ProjectOf(applied);
        project.EngineRevision = AiEditStudioClient.IntAt(edited, "revision");
        await Stage(project, job, "render", "Rendering the revised draft.", ct);
        if (payload.Selection.Start is not null && payload.Selection.End is not null)
        {
            try
            {
                await engine.CallAsync("studio_render_range", new { projectId = project.EngineProjectId, revision = project.EngineRevision, from = payload.Selection.Start, to = payload.Selection.End, quality = "preview", previewFormat = "vertical", queue = true }, ct);
            }
            catch (StudioException ex)
            {
                Warn(project, "Range render was not used: " + ex.Message);
                await engine.CallAsync("studio_render", new { projectId = project.EngineProjectId, revision = project.EngineRevision, preview = true, previewFormat = "vertical", queue = true }, ct);
            }
        }
        else
        {
            await engine.CallAsync("studio_render", new { projectId = project.EngineProjectId, revision = project.EngineRevision, preview = true, previewFormat = "vertical", queue = true }, ct);
        }
        var ready = await engine.WaitUntilIdleAsync(project.EngineProjectId, TimeSpan.FromMinutes(8), ct);
        var videoKey = await engine.CopyExportAsync(ready, storage, ct) ?? throw new StudioException("render_missing", "The revision rendered no MP4.");
        var file = await storage.MaterializeAsync(videoKey, ct);
        var rendered = await probe.ProbeAsync(file, ct);
        var asset = AddAsset(project, AssetKind.DerivedVideo, "revision.mp4", "video/mp4", videoKey, new FileInfo(file).Length, rendered);
        project.CurrentVideoAssetId = asset.Id;
        var timelineJson = "{}";
        try { timelineJson = (await engine.CallAsync("studio_timeline", new { projectId = project.EngineProjectId }, ct)).GetRawText(); }
        catch (StudioException ex) { Warn(project, ex.Message); }
        AddRevision(project, change.Summary, timelineJson, JsonSerializer.Serialize(change, Json), asset.Id, true, change.TouchesCover, "agent");
        if (change.TouchesCover)
        {
            var source = project.Assets.First(a => a.Kind == AssetKind.SourceVideo);
            await MakeCover(project, await storage.MaterializeAsync(source.StorageKey, ct), source.DurationSeconds ?? 1, change.CoverHeadline ?? project.Title, change.CoverPrompt, job, ct);
        }
        project.Status = ProjectStatus.ReadyForReview;
        project.StatusDetail = "Revision is ready to review.";
        project.UpdatedAt = DateTimeOffset.UtcNow;
        project.WarningsJson = JsonSerializer.Serialize(Warnings(project));
        await db.SaveChangesAsync(ct);
        await Stage(project, job, "done", project.StatusDetail, ct);
    }

    private async Task Export(Project project, GenerationJob job, CancellationToken ct)
    {
        var video = project.Assets.FirstOrDefault(a => a.Id == project.CurrentVideoAssetId) ?? throw new StudioException("nothing_to_export", "There is no rendered video yet.");
        await Stage(project, job, "export", "Packaging the reviewed video, cover and captions.", ct);
        string captionKey;
        var captionNote = "stored-transcript";
        if (!string.IsNullOrWhiteSpace(project.EngineProjectId))
        {
            try
            {
                var srt = await engine.CallAsync("studio_export_timeline", new { projectId = project.EngineProjectId, format = "srt", fps = 30 }, ct);
                var content = AiEditStudioClient.Text(srt, "content");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                    captionKey = (await storage.SaveAsync(stream, "application/x-subrip", ".srt", ct)).Key;
                    captionNote = "engine-timeline-srt";
                    goto store;
                }
            }
            catch (StudioException ex) { Warn(project, "SRT export: " + ex.Message); }
        }
        var words = project.Transcripts.OrderByDescending(t => t.CreatedAt).FirstOrDefault();
        var cues = words is null ? [] : JsonSerializer.Deserialize<List<TimedWord>>(words.WordsJson, Json) ?? [];
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(ToSrt(cues))))
            captionKey = (await storage.SaveAsync(stream, "application/x-subrip", ".srt", ct)).Key;
        store:
        var captionAsset = AddAsset(project, AssetKind.CaptionFile, "captions.srt", "application/x-subrip", captionKey, 0, null);
        var cover = project.Covers.FirstOrDefault(c => c.Selected);
        var manifest = JsonSerializer.Serialize(new
        {
            project.Id,
            revision = project.CurrentRevisionNumber,
            video = video.StorageKey,
            cover = cover?.AssetId,
            captions = captionNote,
            note = "No network publish was performed.",
        });
        await using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var manifestStored = await storage.SaveAsync(manifestStream, "application/json", ".json", ct);
        AddAsset(project, AssetKind.ExportManifest, "manifest.json", "application/json", manifestStored.Key, manifestStored.Size, null);
        db.Exports.Add(new ExportBundle
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            RevisionNumber = project.CurrentRevisionNumber,
            VideoAssetId = video.Id,
            CoverAssetId = cover?.AssetId,
            CaptionAssetId = captionAsset.Id,
            SocialCaption = project.PublishPackJson,
            HashtagsJson = "[]",
            ManifestJson = manifest,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        project.Status = ProjectStatus.Exported;
        project.StatusDetail = "Export package is ready to download.";
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Stage(project, job, "done", project.StatusDetail, ct);
    }

    private async Task MakeCover(Project project, string videoPath, double duration, string headline, string? prompt, GenerationJob job, CancellationToken ct)
    {
        var at = Math.Clamp(duration / 3, 0, Math.Max(0, duration - 0.1));
        var temp = Path.Combine(Path.GetTempPath(), "aicontent", Guid.NewGuid().ToString("N") + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        var rtl = (project.Brief?.Language ?? "").ToLowerInvariant() is "fa" or "persian" or "farsi" or "auto";
        await covers.ComposeAsync(new CoverComposeRequest(videoPath, at, string.IsNullOrWhiteSpace(headline) ? project.Title : headline, Theme(project, "primary", "#1C1917"), Theme(project, "accent", "#C4552A"), Theme(project, "text", "#F4EFE6"), Theme(project, "font", rtl ? "Noto Naskh Arabic" : "Noto Sans"), rtl, temp), ct);
        var source = "template";
        var output = temp;
        if (images.IsConfigured && !string.IsNullOrWhiteSpace(prompt))
        {
            var attempt = await images.TryEditAsync(temp, prompt, ct);
            if (attempt.Succeeded && attempt.OutputPath is not null) { source = "generative"; output = attempt.OutputPath; }
            else if (!string.IsNullOrWhiteSpace(attempt.Warning)) Warn(project, attempt.Warning);
        }
        await using var stream = File.OpenRead(output);
        var stored = await storage.SaveAsync(stream, "image/jpeg", ".jpg", ct);
        var asset = AddAsset(project, AssetKind.CoverImage, "cover.jpg", "image/jpeg", stored.Key, stored.Size, null);
        foreach (var existing in db.Covers.Where(c => c.ProjectId == project.Id)) existing.Selected = false;
        var cover = new CoverVariant
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            RevisionNumber = project.CurrentRevisionNumber + 1,
            AssetId = asset.Id,
            Source = source,
            Prompt = prompt,
            Headline = headline,
            Selected = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Covers.Add(cover);
        project.SelectedCoverAssetId = cover.Id;
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> SaveEngineBrand(Project project, EditorialPlan plan, CancellationToken ct)
    {
        var name = "kit" + project.Id.ToString("N")[..16];
        try
        {
            await engine.CallAsync("studio_brand", new
            {
                action = "save",
                name,
                primary = Theme(project, "primary", "#1C1917"),
                accent = Theme(project, "accent", "#C4552A"),
                text = Theme(project, "text", "#F4EFE6"),
                captionStyle = Theme(project, "captionStyle", "bold"),
                captionPosition = Theme(project, "captionPosition", "low"),
                look = plan.Look is "cinema" or "neutral" or "vivid" or "noir" ? plan.Look : "cinema",
                cta = Clip(project.Brief?.CallToAction ?? "", 100),
                label = Clip(project.Title, 70),
                hashtags = plan.Hashtags.Take(8).ToArray(),
            }, ct);
            return name;
        }
        catch (StudioException ex)
        {
            Warn(project, "Brand kit was kept locally. Engine brand save: " + ex.Message);
            return null;
        }
    }

    private async Task SaveTranscript(Project project, TranscriptDraft draft, CancellationToken ct)
    {
        var text = string.Join(" ", draft.Words.Select(w => w.Text));
        db.Transcripts.Add(new TranscriptDocument
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Language = draft.Language,
            Provider = draft.Provider,
            Model = draft.Model,
            WordTiming = draft.WordTiming,
            Text = text,
            WordsJson = JsonSerializer.Serialize(draft.Words, Json),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static object Operation(EditOperation op, double? musicGain)
    {
        var map = new Dictionary<string, object?> { ["op"] = op.Op };
        void Put(string key, object? value) { if (value is not null) map[key] = value; }
        Put("clip", op.Clip);
        Put("from", op.From);
        Put("to", op.To);
        Put("at", op.At);
        Put("start", op.Start);
        Put("end", op.End);
        Put("time", op.Time);
        Put("style", op.Style);
        Put("position", op.Position);
        Put("gain", op.Gain);
        Put("index", op.Index);
        if (op.Op is "add_text" or "update_text") Put("text", op.Text);
        if (op.Op == "add_marker") Put("label", op.Text ?? "note");
        if (op.Op == "set_options") map["options"] = new { musicGain = musicGain ?? 0.15, duckMusic = true };
        return map;
    }

    private void AddRevision(Project project, string summary, string timeline, string? diff, Guid? videoId, bool video, bool cover, string author)
    {
        var number = project.CurrentRevisionNumber + 1;
        db.Revisions.Add(new TimelineRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = number,
            ParentNumber = project.CurrentRevisionNumber == 0 ? null : project.CurrentRevisionNumber,
            State = RevisionState.Applied,
            Summary = Clip(summary, 400),
            TimelineJson = timeline,
            DiffJson = diff,
            EngineRevision = project.EngineRevision,
            VideoAssetId = videoId,
            Author = author,
            TouchesVideo = video,
            TouchesCover = cover,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        project.CurrentRevisionNumber = number;
    }

    private Asset AddAsset(Project project, AssetKind kind, string name, string type, string key, long size, ProbeResult? media)
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = kind,
            StorageKey = key,
            OriginalFileName = name,
            ContentType = type,
            SizeBytes = size,
            DurationSeconds = media?.Duration,
            Width = media?.Width,
            Height = media?.Height,
            Immutable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(asset);
        return asset;
    }

    private async Task Stage(Project project, GenerationJob job, string stage, string message, CancellationToken ct)
    {
        var cancelled = await db.Jobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.CancelRequested).FirstAsync(ct);
        if (cancelled) throw new OperationCanceledException("The job was cancelled.");
        await progress.PublishAsync(project.OwnerId, project.Id, job.Id, stage, message, ct);
    }

    private async Task Cancel(Project project, GenerationJob job, CancellationToken ct)
    {
        project.Status = ProjectStatus.Cancelled;
        project.StatusDetail = "Cancelled.";
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await progress.PublishAsync(project.OwnerId, project.Id, job.Id, "cancelled", "Cancelled.", ct);
        await jobs.CompleteAsync(job.Id, JobStatus.Cancelled, null, ct);
    }

    private static int ClampDuration(ProjectBrief brief)
    {
        var cap = brief.Platform == "post" ? 60 : 90;
        return Math.Clamp(brief.TargetDurationSeconds, 5, cap);
    }

    private static string EnginePlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "post" => "feed",
        "story" => "reels",
        _ => "reels",
    };

    private static string EngineAspect(string platform) => platform.ToLowerInvariant() == "post" ? "4:5" : "9:16";

    private static string Theme(Project project, string key, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(project.ThemeSnapshotJson) ? "{}" : project.ThemeSnapshotJson);
            if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch { /* snapshot is data */ }
        return fallback;
    }

    private static string Excerpt(TranscriptDraft? draft)
    {
        if (draft is null || draft.Words.Count == 0) return "";
        var text = string.Join(" ", draft.Words.Take(80).Select(w => w.Text));
        return text.Length > 800 ? text[..800] : text;
    }

    private static string Clip(string? value, int max)
    {
        var text = (value ?? "").Replace('\0', ' ').Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static void Warn(Project project, string message)
    {
        var list = Warnings(project);
        list.Add(message);
        project.WarningsJson = JsonSerializer.Serialize(list);
    }

    private static List<string> Warnings(Project project)
    {
        try { return JsonSerializer.Deserialize<List<string>>(project.WarningsJson) ?? []; }
        catch { return []; }
    }

    private static string ToSrt(IReadOnlyList<TimedWord> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            sb.Append(i + 1).Append('\n');
            sb.Append(Clock(words[i].Start)).Append(" --> ").Append(Clock(words[i].End)).Append('\n');
            sb.Append(words[i].Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string Clock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}");
    }
}
